using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Web;
using BaiduBackup.Models;

namespace BaiduBackup.Services;

public class BaiduNetdiskService
{
    private readonly HttpClient _http;
    private readonly ILogger<BaiduNetdiskService> _logger;
    private const string TokenUrl = "https://openapi.baidu.com/oauth/2.0/token";
    /// <summary>xpan 文件操作 API 基地址（precreate / create / filemetas / filemanager）</summary>
    private const string XpanApiBase = "https://pan.baidu.com/rest/2.0/xpan/file";
    /// <summary>分片上传 API 基地址（superfile2 upload）</summary>
    private const string PcsApiBase = "https://d.pcs.baidu.com/rest/2.0/pcs/superfile2";
    private const int ChunkSize = 4 * 1024 * 1024; // 4MB
    /// <summary>create 合并分片最大重试次数（针对 errno=-1 block miss）</summary>
    private const int MaxMergeRetries = 3;
    /// <summary>create 重试延迟（秒），渐进式：5, 15, 30</summary>
    private static readonly int[] MergeRetryDelays = [5, 15, 30];
    /// <summary>整个上传流程（重新上传分片+合并）的最大重试次数</summary>
    private const int MaxUploadRetries = 2;

    public BaiduNetdiskService(IHttpClientFactory httpClientFactory, ILogger<BaiduNetdiskService> logger)
    {
        _http = httpClientFactory.CreateClient("BaiduApi");
        _logger = logger;
    }

    public async Task<TokenResponse> ExchangeCodeForTokenAsync(
        string clientId, string clientSecret, string code, string redirectUri)
    {
        var formData = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["redirect_uri"] = redirectUri
        };

        return await RequestTokenAsync(formData);
    }

    public async Task<TokenResponse> RefreshTokenAsync(
        string refreshToken, string clientId, string clientSecret)
    {
        var formData = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret
        };

        return await RequestTokenAsync(formData);
    }

    /// <summary>
    /// 安全读取响应字符串，绕过 .NET 对 charset=utf8（无横杠）的不兼容问题。
    /// 百度 PCS/xpan API 返回 Content-Type: charset=utf8，.NET 只认 utf-8。
    /// </summary>
    private static async Task<string> ReadResponseStringAsync(HttpResponseMessage resp)
    {
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// 安全解析百度 API 响应 JSON。若响应不是有效 JSON（如 HTML 错误页），
    /// 记录原始内容并抛出带上下文的异常，方便排查。
    /// </summary>
    private JsonDocument ParseBaiduJson(string rawJson, string context)
    {
        try
        {
            return JsonDocument.Parse(rawJson);
        }
        catch (JsonException ex)
        {
            var preview = rawJson.Length > 500 ? rawJson[..500] : rawJson;
            _logger.LogError(ex, "百度API返回非JSON (上下文: {Context})，原始响应: {Raw}", context, preview);
            throw new InvalidOperationException(
                $"百度API返回异常响应 (上下文: {context})，请检查 access_token 是否有效。响应预览: {preview}", ex);
        }
    }

    /// <summary>安全读取 JSON 字符串字段，自动处理 Number→String 转换</summary>
    private static string? SafeGetString(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(), // 数字→字符串
            _ => v.ToString() // 其他类型
        };
    }

    private async Task<TokenResponse> RequestTokenAsync(Dictionary<string, string> formData)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsync(TokenUrl, new FormUrlEncodedContent(formData));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"无法连接百度 OAuth 服务器: {ex.Message}", ex);
        }

        var json = await ReadResponseStringAsync(response);
        _logger.LogDebug("Token 响应: HTTP {Code}, Body: {Json}", (int)response.StatusCode, json);

        using var doc = ParseBaiduJson(json, "获取Token");

        if (doc.RootElement.TryGetProperty("access_token", out var accessToken))
        {
            return new TokenResponse
            {
                AccessToken = SafeGetString(doc.RootElement, "access_token")!,
                RefreshToken = SafeGetString(doc.RootElement, "refresh_token"),
                ExpiresIn = doc.RootElement.TryGetProperty("expires_in", out var ei)
                    && ei.ValueKind == JsonValueKind.Number ? ei.GetInt64() : 0
            };
        }

        var errorCode = SafeGetString(doc.RootElement, "error") ?? "";
        var errorDesc = SafeGetString(doc.RootElement, "error_description") ?? "未知错误";
        throw new InvalidOperationException($"Token请求失败: [{errorCode}] {errorDesc}");
    }

    public async Task<string> UploadFileAsync(
        string accessToken, Stream fileStream, string uploadPath, string fileName,
        Action<int, int>? progressCallback = null)
    {
        var fileSize = fileStream.Length;

        // 检查文件大小
        const long MaxFileSize = 4L * 1024 * 1024 * 1024;
        if (fileSize > MaxFileSize)
            throw new InvalidOperationException($"文件过大 ({fileSize / 1024.0 / 1024.0:F1}MB)，百度网盘单文件最大 4GB");

        // 1. 计算分片 MD5 + 整文件 MD5（与 Python baidupan SDK 一致）
        var blockList = await ComputeBlockMd5sAsync(fileStream, fileSize);
        fileStream.Position = 0;
        var contentMd5 = await ComputeFileMd5Async(fileStream, fileSize);
        fileStream.Position = 0;

        var totalChunks = blockList.Count;
        _logger.LogInformation("MD5计算完成: 分片={Count}, content-md5={ContentMd5}", totalChunks, contentMd5);

        var finalPath = uploadPath.TrimEnd('/') + "/" + fileName;

        // 2. 上传到临时路径 → 删旧文件 → rename 到最终路径（覆盖策略）
        var tempPath = uploadPath.TrimEnd('/') + "/" + fileName + "." +
                       DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString() + ".uploading";

        // 先删除旧的最终文件
        await DeleteFileAsync(accessToken, finalPath);

        _logger.LogInformation("上传: 临时={TempPath} → 最终={FinalPath}, 大小={Size}MB",
            tempPath, finalPath, fileSize / 1024.0 / 1024.0);

        for (int attempt = 1; attempt <= MaxUploadRetries; attempt++)
        {
            if (attempt > 1)
            {
                _logger.LogWarning("===== 重试上传 (第{Attempt}/{Max}次) =====", attempt, MaxUploadRetries);
                fileStream.Position = 0;
                blockList = await ComputeBlockMd5sAsync(fileStream, fileSize);
                fileStream.Position = 0;
                contentMd5 = await ComputeFileMd5Async(fileStream, fileSize);
                fileStream.Position = 0;
                await Task.Delay(3000);
            }

            try
            {
                // 清理上次残留的临时文件
                await DeleteFileAsync(accessToken, tempPath);

                // 步骤1: precreate（与 Python SDK 参数一致）
                var uploadid = await PreCreateUploadAsync(accessToken, tempPath, fileSize, blockList, contentMd5);

                // 步骤2: 分片上传（multipart/form-data，字段名 "file"）
                var buffer = new byte[ChunkSize];
                for (int i = 0; i < totalChunks; i++)
                {
                    var start = (long)i * ChunkSize;
                    var end = Math.Min(start + ChunkSize, fileSize);
                    var chunkLength = (int)(end - start);

                    fileStream.Position = start;
                    await fileStream.ReadExactlyAsync(buffer, 0, chunkLength);
                    var chunkData = new byte[chunkLength];
                    Array.Copy(buffer, chunkData, chunkLength);

                    int retries = 0;
                    while (retries < 3)
                    {
                        try
                        {
                            await UploadChunkAsync(accessToken, tempPath, uploadid, i, chunkData);
                            break;
                        }
                        catch (InvalidOperationException) { throw; }
                        catch (Exception ex)
                        {
                            retries++;
                            if (retries >= 3) throw;
                            _logger.LogWarning("分片{Id} 重试 {R}/3: {Msg}", i, retries, ex.Message);
                            await Task.Delay(1000 * retries);
                        }
                    }

                    progressCallback?.Invoke(i + 1, totalChunks);
                    if ((i + 1) % 10 == 0 || i == totalChunks - 1)
                        _logger.LogInformation("进度: {D}/{T}", i + 1, totalChunks);
                }

                // 步骤3: create 合并（与 Python SDK 参数一致）
                await CreateSuperFileWithRetryAsync(accessToken, tempPath, fileSize, blockList, uploadid);

                // 步骤4: rename 到最终路径
                await DeleteFileAsync(accessToken, finalPath);
                await RenameFileAsync(accessToken, tempPath, fileName);

                _logger.LogInformation("上传完成: {Path}", finalPath);
                return finalPath;
            }
            catch (InvalidOperationException ex) when (
                (ex.Message.Contains("errno=-1") || ex.Message.Contains("errno=31500"))
                && attempt < MaxUploadRetries)
            {
                _logger.LogWarning(ex, "上传失败，重试完整流程 ({Attempt}/{Max})", attempt, MaxUploadRetries);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("errno="))
            {
                var friendlyMsg = GetBaiduErrorHint(ex.Message);
                throw new InvalidOperationException(friendlyMsg, ex);
            }
        }

        throw new InvalidOperationException("【服务器内部错误】百度服务器繁忙，请稍后重试");
    }

    /// <summary>计算整个文件的 MD5（content-md5 参数）</summary>
    private async Task<string> ComputeFileMd5Async(Stream stream, long fileSize)
    {
        using var md5 = MD5.Create();
        var hash = await md5.ComputeHashAsync(stream);
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }

    private async Task<List<string>> ComputeBlockMd5sAsync(Stream stream, long fileSize)
    {
        var blockList = new List<string>();
        var totalChunks = (int)Math.Ceiling((double)fileSize / ChunkSize);
        var buffer = new byte[ChunkSize];

        for (int i = 0; i < totalChunks; i++)
        {
            var start = (long)i * ChunkSize;
            var end = Math.Min(start + ChunkSize, fileSize);
            var length = (int)(end - start);

            stream.Position = start;
            await stream.ReadExactlyAsync(buffer, 0, length);

            var md5 = MD5.HashData(buffer.AsSpan(0, length));
            blockList.Add(Convert.ToHexStringLower(md5));
        }

        return blockList;
    }

    /// <summary>
    /// xpan create：合并已上传的分片，创建最终文件。
    /// 官方端点: POST /rest/2.0/xpan/file?method=create
    /// uploadid 和 block_list 必须与 precreate 一致。
    /// </summary>
    private async Task CreateSuperFileAsync(
        string accessToken, string path, long size, List<string> blockList, string uploadid)
    {
        var url = $"{XpanApiBase}?method=create" +
                  $"&access_token={HttpUtility.UrlEncode(accessToken)}";

        // create 负责最终创建文件（precreate 设置 autoinit=0，文件尚未创建）
        var payload = new Dictionary<string, string>
        {
            ["path"] = path,
            ["size"] = size.ToString(),
            ["isdir"] = "0",
            ["rtype"] = "3",      // 兜底：万一路径有残留则覆盖
            ["uploadid"] = uploadid,
            ["block_list"] = JsonSerializer.Serialize(blockList)
        };

        _logger.LogInformation("create: {Path}, 大小={Size}MB, 分片={Count}, uploadid={UploadId}",
            path, size / 1024.0 / 1024.0, blockList.Count,
            uploadid[..Math.Min(uploadid.Length, 20)] + "...");

        var body = new FormUrlEncodedContent(payload);
        var resp = await _http.PostAsync(url, body);
        var json = await ReadResponseStringAsync(resp);
        _logger.LogInformation("create 响应: {Json}", json);

        using var doc = ParseBaiduJson(json, "创建文件");
        var errno = doc.RootElement.TryGetProperty("errno", out var en)
            && en.ValueKind == JsonValueKind.Number ? en.GetInt32() : -1;

        if (errno != 0)
        {
            var errMsg = SafeGetString(doc.RootElement, "error_msg") 
                      ?? SafeGetString(doc.RootElement, "errormsg") 
                      ?? "(无错误描述)";
            var requestId = SafeGetString(doc.RootElement, "request_id") ?? "N/A";
            throw new InvalidOperationException(
                $"创建文件失败 [errno={errno}, msg={errMsg}, request_id={requestId}] " +
                $"(路径: {path})");
        }

        var serverMd5 = SafeGetString(doc.RootElement, "md5");
        var fsId = SafeGetString(doc.RootElement, "fs_id");
        _logger.LogInformation("create 成功: fs_id={FsId}, md5={Md5}", fsId, serverMd5);
    }

    /// <summary>
    /// 带渐进式重试的 create，处理 errno=-1 (block miss)。
    /// create 目标是临时路径，不应出现 errno=31353（文件冲突）。
    /// </summary>
    private async Task CreateSuperFileWithRetryAsync(
        string accessToken, string path, long size, List<string> blockList, string uploadid)
    {
        for (int i = 0; i < MaxMergeRetries; i++)
        {
            try
            {
                await CreateSuperFileAsync(accessToken, path, size, blockList, uploadid);
                return; // 成功
            }
            catch (InvalidOperationException ex) when (
                ex.Message.Contains("errno=-1") && i < MaxMergeRetries - 1)
            {
                var delay = MergeRetryDelays[i];
                _logger.LogWarning(ex,
                    "create 合并失败 (errno=-1 block miss)，" +
                    "第 {Attempt}/{Max} 次重试，等待 {Delay}秒...",
                    i + 1, MaxMergeRetries, delay);
                await Task.Delay(delay * 1000);
            }
        }
    }


    /// <summary>
    /// xpan precreate：预创建上传会话，获取服务器颁发的 uploadid。
    /// 参数与 Python baidupan SDK 保持一致：autoinit=1, rtype=3, content-md5。
    /// </summary>
    private async Task<string> PreCreateUploadAsync(
        string accessToken, string path, long size, List<string> blockList, string contentMd5)
    {
        var url = $"{XpanApiBase}?method=precreate" +
                  $"&access_token={HttpUtility.UrlEncode(accessToken)}";

        var payload = new Dictionary<string, string>
        {
            ["path"] = path,
            ["size"] = size.ToString(),
            ["isdir"] = "0",
            ["autoinit"] = "1",
            ["rtype"] = "3",
            ["block_list"] = JsonSerializer.Serialize(blockList),
            ["content-md5"] = contentMd5
        };

        _logger.LogInformation("precreate: {Path}, 大小={Size}MB, 分片={Count}, content-md5={Md5}",
            path, size / 1024.0 / 1024.0, blockList.Count, contentMd5);

        var body = new FormUrlEncodedContent(payload);
        var resp = await _http.PostAsync(url, body);
        var json = await ReadResponseStringAsync(resp);
        _logger.LogInformation("precreate 响应: {Json}", json);

        using var doc = ParseBaiduJson(json, "precreate");
        var errno = doc.RootElement.TryGetProperty("errno", out var en)
            && en.ValueKind == JsonValueKind.Number ? en.GetInt32() : -1;

        if (errno != 0)
        {
            var errMsg = SafeGetString(doc.RootElement, "error_msg")
                      ?? SafeGetString(doc.RootElement, "errormsg")
                      ?? "(无错误描述)";
            var requestId = SafeGetString(doc.RootElement, "request_id") ?? "N/A";
            throw new InvalidOperationException(
                $"precreate 失败 [errno={errno}, msg={errMsg}, request_id={requestId}] (路径: {path})");
        }

        var uploadid = SafeGetString(doc.RootElement, "uploadid");
        if (string.IsNullOrEmpty(uploadid))
            throw new InvalidOperationException("precreate 未返回 uploadid");

        var returnType = doc.RootElement.TryGetProperty("return_type", out var rt)
            && rt.ValueKind == JsonValueKind.Number ? rt.GetInt32() : -1;
        var blocksInfo = "";
        if (doc.RootElement.TryGetProperty("block_list", out var blocks)
            && blocks.ValueKind == JsonValueKind.Array)
        {
            var needUpload = blocks.EnumerateArray()
                .Where(b => b.ValueKind == JsonValueKind.Number)
                .Select(b => b.GetInt32())
                .ToList();
            blocksInfo = $", 需上传=[{string.Join(",", needUpload)}]";
        }
        _logger.LogInformation("precreate 成功: uploadid={UploadId}, return_type={ReturnType}{Blocks}",
            uploadid[..Math.Min(uploadid.Length, 20)] + "...", returnType, blocksInfo);

        return uploadid;
    }

    /// <summary>
    /// 上传单个分片到 PCS 临时存储（使用 precreate 返回的 uploadid）。
    /// 官方端点: POST /rest/2.0/pcs/superfile2?method=upload
    /// </summary>
    private async Task UploadChunkAsync(
        string accessToken, string path, string uploadid, int partseq, byte[] data)
    {
        var url = $"{PcsApiBase}?method=upload" +
                  $"&access_token={HttpUtility.UrlEncode(accessToken)}" +
                  $"&type=tmpfile" +
                  $"&path={HttpUtility.UrlEncode(path)}" +
                  $"&uploadid={HttpUtility.UrlEncode(uploadid)}" +
                  $"&partseq={partseq}";


        // 使用 multipart/form-data（字段名 "file"），与 Python baidupan SDK 一致
        var formContent = new MultipartFormDataContent();
        var chunkContent = new ByteArrayContent(data);
        chunkContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        formContent.Add(chunkContent, "file", "chunk");

        var resp = await _http.PostAsync(url, formContent);
        var json = await ReadResponseStringAsync(resp);

        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("分片{Partseq} HTTP状态异常: {Code}, 响应: {Json}",
                partseq, (int)resp.StatusCode, json);
        }

        using var doc = ParseBaiduJson(json, "分片上传");

        var hasErrno = doc.RootElement.TryGetProperty("errno", out var errno);
        var code = hasErrno && errno.ValueKind == JsonValueKind.Number ? errno.GetInt32() : 0;

        if (hasErrno && code != 0 && code != 31200) // 31200 = chunk already exists
        {
            var errMsg = SafeGetString(doc.RootElement, "errormsg") ?? "(无错误描述)";
            var requestId = SafeGetString(doc.RootElement, "request_id") ?? "N/A";
            throw new InvalidOperationException(
                $"分片{partseq}上传失败 [errno={code}, msg={errMsg}, request_id={requestId}]" +
                $" (数据大小: {data.Length}字节)");
        }
    }

    /// <summary>检查网盘文件是否已存在</summary>
    public async Task<bool> FileExistsAsync(string accessToken, string fullPath)
    {
        var url = $"{XpanApiBase}?method=filemetas" +
                  $"&access_token={HttpUtility.UrlEncode(accessToken)}" +
                  $"&path={HttpUtility.UrlEncode(fullPath)}" +
                  "&dlink=0&extra=0";

        var resp = await _http.GetAsync(url);
        var json = await ReadResponseStringAsync(resp);
        _logger.LogDebug("filemetas 响应: {Json}", json);

        using var doc = ParseBaiduJson(json, "检查文件存在");
        var errno = doc.RootElement.TryGetProperty("errno", out var en)
            && en.ValueKind == JsonValueKind.Number ? en.GetInt32() : -1;

        if (errno == 0)
        {
            var list = doc.RootElement.GetProperty("list");
            if (list.GetArrayLength() > 0)
            {
                _logger.LogInformation("网盘文件已存在，跳过上传: {Path}", fullPath);
                return true;
            }
        }

        _logger.LogInformation("网盘文件不存在: {Path} (errno={Errno})", fullPath, errno);
        return false;
    }

    /// <summary>删除网盘文件（解决冲突后再上传）</summary>
    public async Task DeleteFileAsync(string accessToken, string fullPath)
    {
        var url = $"{XpanApiBase}?method=filemanager&opera=delete&access_token={HttpUtility.UrlEncode(accessToken)}";

        var fileList = JsonSerializer.Serialize(new[] { fullPath });
        var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["async"] = "0",
            ["filelist"] = fileList
        });

        _logger.LogInformation("尝试删除网盘文件: {Path}", fullPath);
        var resp = await _http.PostAsync(url, body);
        var json = await ReadResponseStringAsync(resp);
        _logger.LogDebug("删除文件响应: {Json}", json);

        using var doc = ParseBaiduJson(json, "删除文件");
        var errno = doc.RootElement.TryGetProperty("errno", out var en)
            && en.ValueKind == JsonValueKind.Number ? en.GetInt32() : 0;

        if (errno == 0)
        {
            _logger.LogInformation("网盘文件已删除: {Path}", fullPath);
        }
        else
        {
            // 文件不存在也算成功
            var errMsg = SafeGetString(doc.RootElement, "errormsg") ?? "";
            _logger.LogInformation("删除文件（errno={Errno}, msg={Msg}）: {Path}，忽略继续",
                errno, errMsg, fullPath);
        }
    }

    /// <summary>重命名网盘文件</summary>
    public async Task RenameFileAsync(string accessToken, string oldPath, string newName)
    {
        var url = $"{XpanApiBase}?method=filemanager&opera=rename&access_token={HttpUtility.UrlEncode(accessToken)}";

        var fileList = JsonSerializer.Serialize(new[]
        {
            new { path = oldPath, newname = newName }
        });
        var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["async"] = "0",
            ["filelist"] = fileList,
            ["ondup"] = "overwrite"
        });

        _logger.LogInformation("重命名: {OldPath} → {NewName}", oldPath, newName);
        var resp = await _http.PostAsync(url, body);
        var json = await ReadResponseStringAsync(resp);
        _logger.LogDebug("重命名响应: {Json}", json);

        using var doc = ParseBaiduJson(json, "重命名文件");
        var errno = doc.RootElement.TryGetProperty("errno", out var en)
            && en.ValueKind == JsonValueKind.Number ? en.GetInt32() : 0;

        if (errno != 0)
        {
            var errMsg = SafeGetString(doc.RootElement, "errormsg") ?? "(无错误描述)";
            _logger.LogWarning("重命名失败 [errno={Errno}, msg={Msg}]: {OldPath} → {NewName}",
                errno, errMsg, oldPath, newName);
            // 不抛异常：文件已在网盘，只是名字是临时名
        }
        else
        {
            _logger.LogInformation("重命名成功: {NewName}", newName);
        }
    }

    /// <summary>将百度 errno 错误信息转为用户友好的中文提示</summary>
    private static string GetBaiduErrorHint(string rawError)
    {
        // 尝试从错误消息中提取 errno
        var match = System.Text.RegularExpressions.Regex.Match(rawError, @"errno=(-?\d+)");
        if (!match.Success) return rawError;

        var code = int.Parse(match.Groups[1].Value);
        var hint = code switch
        {
            -1   => "【服务器内部错误】百度服务器繁忙，请稍后重试",
            2    => "【参数错误】上传请求参数不正确",
            3    => "【文件不存在】目标文件路径不存在",
            6    => "【没有权限】access_token 可能已过期或没有文件操作权限，请重新获取授权",
            110  => "【Token无效】access_token 无效或已过期，请重新授权",
            111  => "【Token过期】access_token 已过期，需要刷新或重新授权",
            31020 => "【存储不足】百度网盘存储空间不足",
            31023 => "【请求频率限制】请求太频繁，请稍后重试",
            31034 => "【触发秒传】文件已在网盘中存在（秒传检测）",
            31183 => "【文件数量超限】目录内文件数量超过限制",
            31201 => "【分片校验失败】文件MD5校验失败，可能是数据在传输中损坏",
            31202 => "【分片列表不完整】缺少部分分片数据",
            31205 => "【文件名冲突】目标路径已存在同名文件",
            31326 => "【网盘空间不足】百度网盘剩余空间不足，无法上传",
            31353 => "【文件冲突】目标路径已存在文件（已改用临时路径方案，此错误不应再出现，请联系开发者）",
            31500 => "【秒传校验失败】百度已关闭秒传功能，已自动切换为普通上传",
            31363 => "【分片合并失败】上传会话过期或分片不完整，重试即可恢复",
            _    => rawError
        };

        return hint;
    }
}
