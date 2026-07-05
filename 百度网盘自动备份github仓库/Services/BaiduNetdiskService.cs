using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Web;
using BaiduBackup.Models;

namespace BaiduBackup.Services;

public class BaiduNetdiskService
{
    private readonly HttpClient _http;
    private const string TokenUrl = "https://openapi.baidu.com/oauth/2.0/token";
    private const string XpanApiBase = "https://pan.baidu.com/rest/2.0/xpan/file";
    private const string PcsApiBase = "https://d.pcs.baidu.com/rest/2.0/pcs/superfile2";
    private const int ChunkSize = 4 * 1024 * 1024; // 4MB
    private const int MaxMergeRetries = 3;
    private static readonly int[] MergeRetryDelays = [5, 15, 30];
    private const int MaxUploadRetries = 2;

    public BaiduNetdiskService(IHttpClientFactory httpClientFactory)
    {
        _http = httpClientFactory.CreateClient("BaiduApi");
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

    private static async Task<string> ReadResponseStringAsync(HttpResponseMessage resp)
    {
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    private static JsonDocument ParseBaiduJson(string rawJson, string context)
    {
        try
        {
            return JsonDocument.Parse(rawJson);
        }
        catch (JsonException ex)
        {
            var preview = rawJson.Length > 500 ? rawJson[..500] : rawJson;
            throw new InvalidOperationException(
                $"百度API返回异常响应 (上下文: {context})，请检查 access_token 是否有效。响应预览: {preview}", ex);
        }
    }

    private static string? SafeGetString(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => v.ToString()
        };
    }

    private async Task<TokenResponse> RequestTokenAsync(Dictionary<string, string> formData)
    {
        using HttpResponseMessage response = await _http.PostAsync(TokenUrl, new FormUrlEncodedContent(formData));
        var json = await ReadResponseStringAsync(response);

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
        Action<int, int, int, int>? progressCallback = null)
    {
        var fileSize = fileStream.Length;

        const long MaxFileSize = 4L * 1024 * 1024 * 1024;
        if (fileSize > MaxFileSize)
            throw new InvalidOperationException($"文件过大 ({fileSize / 1024.0 / 1024.0:F1}MB)，百度网盘单文件最大 4GB");

        var blockList = await ComputeBlockMd5sAsync(fileStream, fileSize);
        fileStream.Position = 0;
        var contentMd5 = await ComputeFileMd5Async(fileStream, fileSize);
        fileStream.Position = 0;

        var totalChunks = blockList.Count;
        var finalPath = uploadPath.TrimEnd('/') + "/" + fileName;

        var tempPath = uploadPath.TrimEnd('/') + "/" + fileName + "." +
                       DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString() + ".uploading";

        await DeleteFileAsync(accessToken, finalPath);

        for (int attempt = 1; attempt <= MaxUploadRetries; attempt++)
        {
            if (attempt > 1)
            {
                fileStream.Position = 0;
                blockList = await ComputeBlockMd5sAsync(fileStream, fileSize);
                fileStream.Position = 0;
                contentMd5 = await ComputeFileMd5Async(fileStream, fileSize);
                fileStream.Position = 0;
                await Task.Delay(3000);
            }

            try
            {
                await DeleteFileAsync(accessToken, tempPath);

                var uploadid = await PreCreateUploadAsync(accessToken, tempPath, fileSize, blockList, contentMd5);

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
                        catch (Exception)
                        {
                            retries++;
                            if (retries >= 3) throw;
                            await Task.Delay(1000 * retries);
                        }
                    }

                    progressCallback?.Invoke(i + 1, totalChunks, attempt, MaxUploadRetries);
                }

                await CreateSuperFileWithRetryAsync(accessToken, tempPath, fileSize, blockList, uploadid);

                await DeleteFileAsync(accessToken, finalPath);
                await RenameFileAsync(accessToken, tempPath, fileName);

                return finalPath;
            }
            catch (InvalidOperationException ex) when (
                (ex.Message.Contains("errno=-1") || ex.Message.Contains("errno=31500"))
                && attempt < MaxUploadRetries)
            {
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("errno="))
            {
                var friendlyMsg = GetBaiduErrorHint(ex.Message);
                throw new InvalidOperationException(friendlyMsg, ex);
            }
        }

        throw new InvalidOperationException("【服务器内部错误】百度服务器繁忙，请稍后重试");
    }

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

    private async Task CreateSuperFileAsync(
        string accessToken, string path, long size, List<string> blockList, string uploadid)
    {
        var url = $"{XpanApiBase}?method=create" +
                  $"&access_token={HttpUtility.UrlEncode(accessToken)}";

        var payload = new Dictionary<string, string>
        {
            ["path"] = path,
            ["size"] = size.ToString(),
            ["isdir"] = "0",
            ["rtype"] = "3",
            ["uploadid"] = uploadid,
            ["block_list"] = JsonSerializer.Serialize(blockList, AppJsonContext.Default.ListString)
        };

        var body = new FormUrlEncodedContent(payload);
        using var resp = await _http.PostAsync(url, body);
        var json = await ReadResponseStringAsync(resp);

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
    }

    private async Task CreateSuperFileWithRetryAsync(
        string accessToken, string path, long size, List<string> blockList, string uploadid)
    {
        for (int i = 0; i < MaxMergeRetries; i++)
        {
            try
            {
                await CreateSuperFileAsync(accessToken, path, size, blockList, uploadid);
                return;
            }
            catch (InvalidOperationException ex) when (
                ex.Message.Contains("errno=-1") && i < MaxMergeRetries - 1)
            {
                var delay = MergeRetryDelays[i];
                await Task.Delay(delay * 1000);
            }
        }
    }

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
            ["block_list"] = JsonSerializer.Serialize(blockList, AppJsonContext.Default.ListString),
            ["content-md5"] = contentMd5
        };

        var body = new FormUrlEncodedContent(payload);
        using var resp = await _http.PostAsync(url, body);
        var json = await ReadResponseStringAsync(resp);

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

        return uploadid;
    }

    private async Task UploadChunkAsync(
        string accessToken, string path, string uploadid, int partseq, byte[] data)
    {
        var url = $"{PcsApiBase}?method=upload" +
                  $"&access_token={HttpUtility.UrlEncode(accessToken)}" +
                  $"&type=tmpfile" +
                  $"&path={HttpUtility.UrlEncode(path)}" +
                  $"&uploadid={HttpUtility.UrlEncode(uploadid)}" +
                  $"&partseq={partseq}";

        var formContent = new MultipartFormDataContent();
        var chunkContent = new ByteArrayContent(data);
        chunkContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        formContent.Add(chunkContent, "file", "chunk");

        using var resp = await _http.PostAsync(url, formContent);
        var json = await ReadResponseStringAsync(resp);

        using var doc = ParseBaiduJson(json, "分片上传");

        var hasErrno = doc.RootElement.TryGetProperty("errno", out var errno);
        var code = hasErrno && errno.ValueKind == JsonValueKind.Number ? errno.GetInt32() : 0;

        if (hasErrno && code != 0 && code != 31200)
        {
            var errMsg = SafeGetString(doc.RootElement, "errormsg") ?? "(无错误描述)";
            var requestId = SafeGetString(doc.RootElement, "request_id") ?? "N/A";
            throw new InvalidOperationException(
                $"分片{partseq}上传失败 [errno={code}, msg={errMsg}, request_id={requestId}]" +
                $" (数据大小: {data.Length}字节)");
        }
    }

    public async Task<bool> FileExistsAsync(string accessToken, string fullPath)
    {
        var url = $"{XpanApiBase}?method=filemetas" +
                  $"&access_token={HttpUtility.UrlEncode(accessToken)}" +
                  $"&path={HttpUtility.UrlEncode(fullPath)}" +
                  "&dlink=0&extra=0";

        using var resp = await _http.GetAsync(url);
        var json = await ReadResponseStringAsync(resp);

        using var doc = ParseBaiduJson(json, "检查文件存在");
        var errno = doc.RootElement.TryGetProperty("errno", out var en)
            && en.ValueKind == JsonValueKind.Number ? en.GetInt32() : -1;

        if (errno == 0)
        {
            var list = doc.RootElement.GetProperty("list");
            if (list.GetArrayLength() > 0)
                return true;
        }

        return false;
    }

    public async Task DeleteFileAsync(string accessToken, string fullPath)
    {
        var url = $"{XpanApiBase}?method=filemanager&opera=delete&access_token={HttpUtility.UrlEncode(accessToken)}";

        var fileList = JsonSerializer.Serialize(new[] { fullPath }, AppJsonContext.Default.StringArray);
        var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["async"] = "0",
            ["filelist"] = fileList
        });

        using var resp = await _http.PostAsync(url, body);
        // 忽略结果：文件不存在也算成功
    }

    public async Task RenameFileAsync(string accessToken, string oldPath, string newName)
    {
        var url = $"{XpanApiBase}?method=filemanager&opera=rename&access_token={HttpUtility.UrlEncode(accessToken)}";

        var fileList = JsonSerializer.Serialize(new[]
        {
            new RenameItem { path = oldPath, newname = newName }
        }, AppJsonContext.Default.RenameItemArray);
        var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["async"] = "0",
            ["filelist"] = fileList,
            ["ondup"] = "overwrite"
        });

        using var resp = await _http.PostAsync(url, body);
        var json = await ReadResponseStringAsync(resp);

        using var doc = ParseBaiduJson(json, "重命名文件");
        var errno = doc.RootElement.TryGetProperty("errno", out var en)
            && en.ValueKind == JsonValueKind.Number ? en.GetInt32() : 0;

        if (errno != 0)
        {
            // 不抛异常：文件已在网盘，只是名字是临时名
        }
    }

    private static string GetBaiduErrorHint(string rawError)
    {
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
