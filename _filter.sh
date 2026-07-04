#!/bin/bash
# 注意: filter-branch 在临时目录中运行此脚本，所以用相对路径
TARGET="百度网盘自动备份github仓库/wwwroot/index.html"
if [ -f "$TARGET" ]; then
  sed -i.bak 's/value="iiYIlIMnnBUq8OGklXDULJFZzqYHGiF7"/value=""/g' "$TARGET"
  sed -i.bak 's/value="HtLwtUNGRaFDjWbohN86ECPMxBVyhFip"/value=""/g' "$TARGET"
  rm -f "$TARGET.bak"
fi
