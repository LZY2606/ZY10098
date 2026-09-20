# Pair-wise GSB

本地运行的 EDA 网表连接语义比较系统。系统保存两份网表原文与规则原文，基于内容指纹创建比较会话，执行层次展开、端口/网络/参数别名规范化、电阻阵列拆分、可交换引脚匹配和受约束图同构搜索。

## 能力

- 保留网表、规则原文，并分别保存规范化内容指纹和规则版本。
- 展开电阻阵列和层次模块；支持器件改名、端口别名、网络别名、参数单位前缀和可交换引脚。
- 前置诊断包括电源/地短接、悬空端口、层次引用循环、未声明网络、重复引脚和参数单位不一致。
- 搜索状态区分 `Equivalent`、`NonEquivalent`、`SearchTimeout`、`LockConflict` 和 `DiagnosticFailure`；超时不会显示为不等价。
- 页面展示匹配组件、候选歧义、旧会话映射建议、最小区分子图和可验证映射证书。
- 锁定或否决后可继续搜索；锁冲突证书返回导致冲突的最小锁定集合。
- 比较会话绑定左右网表指纹和规则指纹；任一输入变化都会创建新会话。旧决定只复制为 `NeedsReview` 建议，不自动生效。
- 映射证书按排序后的组件、引脚和网络计算 SHA-256 摘要，节点遍历顺序变化不会改变摘要。
- 批量比较通过稳定幂等键去重；一次提交是一个事务边界，验证失败不留下部分数据。后台作业可恢复，只有所有对子完成才把结果集发布为 `Published`。
- 所有数据保存在本地 JSON 文件中，不需要云账号或外部数据库。

## 快速开始

```bash
dotnet restore
dotnet test --nologo
dotnet run --project src/Web --urls http://127.0.0.1:5222
```

打开 <http://127.0.0.1:5222>。

默认数据文件位于 `src/Web/data/pairwise-gsb.json`。也可以通过配置覆盖：

```bash
PAIRWISE_GSB_DATA=/tmp/pairwise-gsb.json dotnet run --project src/Web
```

## 网表 JSON

```json
{
  "name": "filter-a",
  "ports": [
    { "id": "in", "name": "IN", "direction": "Input", "net": "n1" },
    { "id": "out", "name": "OUT", "direction": "Output", "net": "n2" }
  ],
  "nets": [
    { "id": "n1", "name": "input" },
    { "id": "n2", "name": "output" }
  ],
  "devices": [
    {
      "id": "r1",
      "name": "R1",
      "type": "resistor",
      "pins": [
        { "pin": "1", "net": "n1" },
        { "pin": "2", "net": "n2" }
      ],
      "parameters": [
        { "key": "RESISTANCE", "numericValue": 1, "unit": "kohm" }
      ]
    }
  ],
  "modules": [],
  "instances": [],
  "powerNetNames": []
}
```

端口方向为 `Input`、`Output`、`InOut`、`Power` 或 `Ground`。电阻阵列规则匹配例如类型 `RN`，并把 `COM/R1/R2/...` 展开为独立二端电阻。层次模块通过模块定义和实例端口映射展开；循环引用会成为错误诊断而不会无限递归。

## 规则 JSON

规则可以为空导入；为空时使用内置默认规则 `rules-2026.09.1`。规则字段：

- `componentAliases`：器件类型别名，例如 `resistor/RES → R`。
- `portAliases`：外部端口别名，例如 `VCC/AVDD → VDD`。
- `netAliases`：网络别名。
- `parameterAliases`：参数名别名，例如 `RESISTANCE → R`。
- `resistorArrays`：阵列类型、公共引脚、元素引脚和电阻参数名。
- `swappablePins`：可交换引脚组，例如无源电阻的 `1/2`。
- `parameterTolerance`：参数数值容差。

规则原文和规范化 JSON 指纹一起保存。规则内容变化即使版本字符串相同，也会产生新的规则指纹和新会话。

## 状态机

- `Created`：会话已创建，尚未完成搜索。
- `Equivalent`：无阻塞诊断且找到满足所有活动锁定/否决的完整双射，已签发证书。
- `NonEquivalent`：搜索完整结束但没有映射；结果附最小区分子图。
- `SearchTimeout`：达到时间预算；结果不能作为不等价结论。
- `LockConflict`：无约束图可匹配，但活动锁定或否决无法共同满足；返回最小责任锁定集合。
- `DiagnosticFailure`：存在错误级前置诊断，例如电源短接、悬空端口、层次循环或单位不一致。

批量状态为 `Pending`、`Running`、`Published`。所有作业成功完成前不发布结果集；作业使用 `{幂等键}:{对子序号}` 稳定标识，崩溃后恢复 `Pending/Faulted` 作业，不会重复生成结果记录。

## HTTP API

- `POST /api/netlists`：导入网表原文。
- `POST /api/rules`：导入规则原文或空内容使用默认规则。
- `POST /api/sessions`：按输入指纹创建会话并立即搜索。
- `GET /api/sessions/{id}`：读取结论、展开图、决定、原文、规则版本和事件顺序。
- `POST /api/sessions/{id}/search`：按当前决定重新搜索。
- `POST /api/sessions/{id}/locks`：锁定一对展开后的组件。
- `POST /api/sessions/{id}/vetos`：否决一对候选。
- `POST /api/sessions/{id}/suggestions/{decisionId}`：采纳或拒绝旧决定建议。
- `GET /api/sessions/{id}/certificate`：读取映射证书。
- `POST /api/certificates/verify`：重新计算并验证证书摘要。
- `POST /api/batches`：幂等提交批量请求。
- `GET /api/batches/{id}`：查看批量作业和发布状态。

## 持久化格式

存储文件是一个规范序列化 JSON 对象，顶层字段包括：

- `formatVersion`：当前为 `1`。
- `nextSequence`：全局事件序号。
- `netlists`：网表 ID 到原文、名称、指纹、创建时间的映射。
- `rules`：规则 ID 到版本、原文、指纹、创建时间的映射。
- `sessions`：会话 ID 到输入 ID、三个指纹、状态、搜索结果和证书的映射。
- `decisions`：锁定、否决和待复核建议。
- `events`：按全局序号追加的审计事件。
- `batches`：批量请求、会话 ID、作业 ID 和发布状态。
- `jobs`：稳定幂等键、尝试次数、运行状态和错误。

写盘流程为：在进程内锁内修改内存文档 → 写入同目录临时文件 → 原子替换目标文件。批量提交在同一次修改内验证所有输入并创建会话/作业；任何验证异常都会阻止替换原文件，因此要么全部生效，要么保持原状。

## 兼容策略

- 旧版本文件由 `formatVersion` 识别；当前版本只执行前向迁移并把版本提升到 `1`。
- 读取到高于当前应用支持的格式版本会快速失败，避免误解释未知语义。
- 新增字段必须使用可空值或默认值，旧文件缺少该字段时按默认行为处理。
- 枚举序列化为稳定的 camelCase 字符串；指纹只依赖规范 JSON，不依赖内存遍历顺序。
- 规则版本是面向用户的可读标识，规则指纹才是会话绑定和证书验证依据。

## 测试

测试覆盖改名与可交换引脚、电阻阵列展开、前置诊断、超时区分、参数差异见证、最小锁冲突、证书顺序稳定性、输入变更新会话、旧决定复核、批量幂等/原子发布和 HTTP 页面工作流。

```bash
dotnet test --nologo
```
