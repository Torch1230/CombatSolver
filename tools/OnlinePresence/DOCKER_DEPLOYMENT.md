# miaovps Docker 部署

连胜排行按安装与存档分组读取 SQLite，只提取结算与筛选所需字段，逐组统计，不将全库战斗明细解析进内存。2026-09-12 线上约 4.7 万局副本验证：四种排行查询在 128MB Node 堆限制下通过，堆占用约 9–12MB；原实现曾触发 256MB 堆耗尽并反复重启。排查“在线恢复中”应结合容器重启次数和 Node fatal 日志，Docker OOMKilled=false 不能排除 Node 自身堆耗尽。

运行目录 `/home/Torch/combatsolver-presence`，用 `docker compose up -d --build` 更新。容器 `combatsolver-presence` 使用 Node.js 24、UID/GID 1000、host 网络；内存上限 512MB，日志轮转 10MB × 3。原端口、TLS 证书、SQLite 数据及登录会话配置原位保留。

`private/docker.env` 为实际环境变量的原始 KEY=value 文件，权限 0600，Compose raw 格式不使用 shell 引号。原 `private/service.env` 保留作回滚参考。`private/` 只读挂载，`data/` 读写挂载，两者不进入镜像。禁止同时运行旧 systemd 服务与容器。

查看状态：`docker compose ps`；资源：`docker stats --no-stream`。更新已发布客户端版本：`docker compose exec monitor node set-release.mjs <版本>`。只有下载渠道发布成功才更新该值。

首次迁移的源码与 SQLite 备份在 `/home/Torch/service-backups/docker-migration`。回滚先停止容器，恢复原应用源码，再启动旧用户级 systemd 服务。恢复数据库前核对迁移后的新增统计。Docker 提供进程隔离、资源限制和自动重启，本身不降低应用所需内存。
