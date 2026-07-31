# 爱发电社区名单

“更多”页面现在支持从爱发电开放接口读取赞助者，并根据累计赞助金额或方案名称生成“特别荣誉”名单。

## 配置

在爱发电创作者后台创建开放 API 凭据。公开主页对应的创作者 `user_id` 已写入配置；在启动 BohemiX 的进程环境中只需设置 token：

```powershell
$env:BOHEMIX_AFDIAN_TOKEN = "你的爱发电 API token"
```

配置模板位于 `src/BohemiX.App/Assets/Community/community-hub.json`。其中：

- `sponsorUrl` 是用户点击“成为赞助者”时打开的主页，当前为 `https://afdian.com/a/Lume_Sky`。
- `specialHonorPlanNames` 填写你在爱发电后台定义的特别荣誉方案名称；匹配方案名称的用户会进入特别荣誉。
- `specialHonorMinimumAmount` 是可选金额兜底，当前模板为 `0`（关闭）；只有你明确设置大于 0 的值时才启用累计金额判定。
- `specialHonorDurationMonths` 当前为 `3`，与“这期神了！”方案中约定的三个月特别荣誉期限一致。
- `maximumPages` 限制最多读取的 API 分页数，避免异常配置导致无限请求。

名单加载会按用户 ID 去重。赞助者按最近支持时间排列，特别荣誉按累计金额排列；爱发电接口失败时会保留内置回退名单。

当前主页公开方案中，`请作者一瓶救世干酒`、`请作者加一次perk（你在这方面升级了！）` 和 `这期神了！` 的支持者都会进入赞助者名单；`这期神了！` 会在最近一次支持后的三个月内同时进入特别荣誉名单。

## 发布安全

不要把 API `token` 写入 JSON、源代码、日志或公开安装包。直接从环境变量读取适合本地或受控部署。面向公开用户发布时，建议让服务器或定时任务持有爱发电 token，生成现有 `contentEndpoint` 所需的标准 JSON，再由客户端只读取该 JSON。

## Echo Cave plan access

To require an Afdian plan before a user can submit an Echo Cave message, configure
`echoCaveEligiblePlanNames` in the `afdian` section:

```json
"echoCaveEligiblePlanNames": [
  "Echo Cave Support"
]
```

An empty array keeps Echo Cave open, preserving the existing feedback behavior. When one or
more names are configured, the app asks for the supporter's Afdian user ID or nickname and
checks that the account's `current_plan.name` exactly matches one of the configured names.
The app checks the plan again immediately before it posts the message and includes the supplied
identifier as `afdianSupporterIdentifier` in the feedback payload.

The Afdian open API does not authenticate the local app user as that supporter. A public
feedback endpoint must therefore repeat this plan lookup with its own protected Afdian token
and reject unverified identifiers; client-side verification is an access-control convenience,
not the trust boundary. Keep the token in `BOHEMIX_AFDIAN_TOKEN` only for controlled builds or,
preferably, on that feedback service.

## Echo Cave endpoint

The repository includes a Cloudflare Worker implementation at
`workers/echo-cave`. It validates the Afdian plan on the server and persists
accepted messages in Cloudflare D1. Follow `workers/echo-cave/README.md` to create
the database, configure the protected `AFDIAN_TOKEN` secret, and deploy it.

After deployment, set `BOHEMIX_ECHO_CAVE_ENDPOINT` to the Worker HTTPS URL before
starting BohemiX. The client reads this through the configuration field
`feedbackEndpointEnvironmentVariable`; this prevents a deployed URL from being
hard-coded into the packaged community configuration.

标准 JSON 形状为：

```json
{
  "sponsorUrl": "https://afdian.com/a/Lume_Sky",
  "specialThanks": [],
  "sponsors": [
    { "name": "支持者", "tier": "方案名称" }
  ]
}
```
