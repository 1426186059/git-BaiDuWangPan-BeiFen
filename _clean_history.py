"""使用 git fast-export/fast-import 清理敏感信息"""
import subprocess, sys, os, re

os.chdir(r'd:\OpenSource\git-BaiDuWangPan-BeiFen')

# 敏感字符串 (原始值及其 base64/blob 引用需处理)
APPKEY = b'iiYIlIMnnBUq8OGklXDULJFZzqYHGiF7'
SECRETKEY = b'HtLwtUNGRaFDjWbohN86ECPMxBVyhFip'

# Step 1: fast-export
print("[1/3] 导出仓库...")
export = subprocess.check_output(
    ['git', 'fast-export', '--all'],
    stderr=subprocess.STDOUT
)

# Step 2: 替换敏感字符串
print("[2/3] 清理敏感内容...")
cleaned = export.replace(APPKEY, b'').replace(SECRETKEY, b'')

if cleaned == export:
    print("  (无需清理)")
else:
    print(f"  已清理 {len(export.split(APPKEY)) - 1} 处 AppKey 和 {len(export.split(SECRETKEY)) - 1} 处 SecretKey")

# Step 3: 重置并 fast-import
print("[3/3] 重建仓库...")
subprocess.run(['git', 'checkout', '--orphan', '_clean_tmp'], check=True, capture_output=True)
subprocess.run(['git', 'reset', '--hard'], check=True)

result = subprocess.run(
    ['git', 'fast-import', '--force', '--quiet'],
    input=cleaned,
    capture_output=True
)
if result.returncode != 0:
    print("fast-import 失败:", result.stderr.decode())
    sys.exit(1)

# 恢复分支指向
subprocess.run(['git', 'checkout', '-B', 'main'], check=True)
subprocess.run(['git', 'branch', '-D', '_clean_tmp'], check=True, capture_output=True)

# 清理 filter-branch 的备份 refs
for ref_dir in ['.git/refs/original', '.git/refs/remotes']:
    pass  # fast-import 不会创建这些

print("✅ 历史清理完成！")
print("请确认无误后执行: git push origin main --force")
