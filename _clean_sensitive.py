"""Git filter-branch 辅助脚本：替换 index.html 中的敏感 AppKey/SecretKey"""
import sys, os

target = '百度网盘自动备份github仓库/wwwroot/index.html'
if not os.path.exists(target):
    sys.exit(0)

with open(target, 'r', encoding='utf-8') as f:
    content = f.read()

# 替换敏感 AppKey 和 SecretKey 为空字符串
content = content.replace(
    'value="iiYIlIMnnBUq8OGklXDULJFZzqYHGiF7"',
    'value=""'
)
content = content.replace(
    'value="HtLwtUNGRaFDjWbohN86ECPMxBVyhFip"',
    'value=""'
)

with open(target, 'w', encoding='utf-8') as f:
    f.write(content)
