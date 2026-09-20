# Pair-wise EDA 网表比较

这是一个完全本地运行的双网表比较系统。它保存原始导入文本，按可版本化规则展开层次、电阻阵列、端口别名和可交换引脚，再用带截止时间的约束搜索判断连接语义。

## 能力

- 保存网表和规则原文，使用规范化 JSON 的 SHA-256 指纹标识版本。
- 会话同时绑定左网表、右网表和规则修订；任一输入变化都会创建新会话。
- 旧会话的人工锁定不会自动套用，只作为新会话中“需要复核”的建议。
- 支持电阻阵列拆分、模块端口别名、网络别名和器件可交换引脚。
- 电源/地短接、悬空端口、层次引用循环、未知模块和参数单位不一致先形成诊断，诊断阻断普通等价结论。
- 区分 `NotEquivalent` 与 `SearchTimeout`；非等价附最小区分子图，等价附可验证证书。
- 用户可锁定器件对、锁定引脚或否决候选；冲突返回最小锁定集合。
- 批量提交以一次状态文件事务为边界：校验失败整体不生效，所有对子完成后才发布结果集。
- 后台任务在应用启动时恢复；同一稳定作业标识重试不会重复计入已完成结果。

## 运行

```bash
dotnet restore
dotnet test --nologo
dotnet run --project src/Web --urls http://127.0.0.1:5222
```

打开 <http://127.0.0.1:5222>。页面可直接填入内置示例，也可以调用 `/api/*`。

## 网表 JSON

系统当前接受 JSON 导入；原始文本原样保存到 `rawText`。

```json
{
  "id": "left-demo",
  "name": "array side",
  "rootModuleId": "top",
  "modules": [
    {
      "id": "top",
      "ports": [{ "id": "A" }, { "id": "B" }],
      "devices": [
        {
          "id": "RA1",
          "type": "resistor_array",
          "pins": { "A1": "A", "B1": "N", "A2": "N", "B2": "B" },
          "parameters": { "count": "2", "resistance": "1 kohm" }
        }
      ],
      "instances": [],
      "nets": []
    }
  ]
}
```

规则 JSON 包含规则版本、搜索截止时间、网络/端口别名、可交换引脚、阵列展开和单位换算。默认规则已经包含 `ohm`、`Ω`、`kohm`。

## HTTP API

- `POST /api/netlists`：保存网表；同一逻辑 ID 内容变化产生新修订。
- `POST /api/rules`：保存规则；规则内容变化产生新修订。
- `POST /api/sessions`：按当前网表和规则修订创建或复用会话。
- `POST /api/sessions/{id}/decisions`：锁定或否决映射。
- `POST /api/sessions/{id}/suggestions/{decisionId}/accept|reject`：复核旧决定。
- `POST /api/sessions/{id}/retry`：使用稳定作业标识重新搜索。
- `POST /api/batches`：幂等批量提交；重复的 `idempotencyKey` 返回同一批次。
- `GET /api/state`：读取完整本地状态快照。
- `POST /api/certificate/verify`：重新计算并校验证书摘要。

## 持久化格式

默认数据目录是运行目录下的 `.netcompare/`，可通过 `NetCompare:DataDirectory` 配置。核心文件是：

- `.netcompare/state.json`：版本化的单一状态文档。
- `state.json.tmp-{pid}`：原子替换时使用的临时文件；成功后立即 `rename` 为 `state.json`。

`state.json` 的顶层结构：

```json
{
  "formatVersion": 1,
  "netlists": { "revisionId": { "netlistId": 1, "revision": 1, "fingerprint": "...", "document": {} } },
  "rules": { "revisionId": { "rulesId": 1, "revision": 1, "fingerprint": "...", "rules": {} } },
  "sessions": { "sessionId": { "state": "Review", "result": {}, "decisions": [], "events": [] } },
  "batches": { "batchId": { "state": "Running", "pairs": [] } }
}
```

枚举以字符串存储，时间为 UTC ISO-8601，指纹和证书为小写十六进制 SHA-256。证书哈希不包含自身哈希字段，也不依赖字典枚举顺序；组件、引脚和网络都先按稳定键排序。

## 兼容策略

- `formatVersion` 是持久化格式主版本。版本 1 的读取器应忽略未知字段，以支持向前兼容的小改动。
- 不允许把未来主版本静默降级；未来版本迁移器必须生成新的格式版本并保留旧文件备份。
- 规则版本字符串由调用方提供，规则内容还另有 SHA-256 指纹；版本号相同但内容变化仍会触发新会话。
- 原始导入文本不做破坏性迁移，规范化结果可从原文和规则重新计算。
- 不使用云账号、外部数据库或外部前端资源。

## 验证

测试项目包含：

- 阵列展开、端口别名、单位归一化和稳定证书摘要。
- 非等价最小区分子图、搜索超时、四类强制诊断。
- 冲突锁定的最小集合。
- 输入/规则指纹变化、旧决定建议复核。
- 批量幂等、全部完成后发布、后台恢复且不重复完成事件。
- 进程内 ASP.NET Core 页面和 API 集成测试。
