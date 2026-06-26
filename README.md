# BOM 视图对比工具 (BomDiffWinform)

基于 .NET 8 + WinForms 的 BOM（物料清单）视图数据对比工具，用于 Oracle 视图数据抽取、本地存储及新旧视图全量差异比对。

## 项目背景

在 PLM（Windchill）系统中，BOM 视图数据存在以下问题：

- 数据量大（10 万～百万级）
- 视图逻辑复杂（版本控制、来源、结构）
- SQL 优化后存在数据一致性风险
- 需要对新旧视图进行全量对比验证

## 功能特性

- Oracle 视图分页抽取（支持百万级数据）
- 本地 SQLite 持久化存储
- 新旧视图全量对比（Dictionary O(1) 高效 Diff）
- 差异数据可视化展示
- 支持取消与断点续跑
- 夜间自动批处理执行（可配置）
- 动态视图字段映射（通过 JSON 配置文件）

## 技术栈

| 技术 | 说明 |
|------|------|
| .NET 8 | 目标框架 |
| WinForms | 桌面 UI |
| Oracle.ManagedDataAccess.Core 23.5.1 | Oracle 数据库访问 |
| System.Data.SQLite.Core 1.0.118 | SQLite 本地存储 |
| Dapper 2.1.35 | 轻量 ORM |
| Serilog | 结构化日志（Console + File + Async） |
| Microsoft.Extensions.Logging | 日志抽象 |

## 项目结构

```
BomDiffWinform/
├── Forms/                       # UI 窗体
│   ├── MainForm.cs             # 主界面（对比任务控制）
│   └── ConfigForm.cs           # 配置管理界面
├── Models/                     # 数据模型
│   └── DynamicBomModels.cs     # 动态 BOM 模型
├── Services/                   # 业务服务
│   ├── OracleService.cs        # Oracle 分页读取服务
│   ├── SQLiteService.cs        # SQLite 本地存储服务
│   ├── DiffService.cs          # 差异对比服务
│   ├── SchemaService.cs        # 表结构动态创建服务
│   ├── ScheduleService.cs      # 夜间定时任务服务
│   ├── DatabaseHelper.cs       # 数据库连接辅助
│   ├── LogService.cs           # 日志服务（Serilog）
│   └── ViewMappingConfigService.cs  # 视图字段映射配置
├── App.config                  # 应用配置文件
├── bom_view_mappings.json      # 视图字段映射配置
└── Program.cs                  # 应用入口
```

## 快速开始

### 环境要求

- .NET 8 SDK
- Oracle 数据库（需可访问 PVS_BOM / PVS_BOM2 视图）
- Windows 操作系统

### 构建运行

```bash
# 还原依赖
dotnet restore

# 构建项目
dotnet build

# 运行程序
dotnet run
```

### 配置说明

编辑 `App.config` 中的关键配置项：

| 配置项 | 说明 | 默认值 |
|--------|------|--------|
| OracleConnectionString | Oracle 连接字符串 | 需修改 |
| OldViewName | 旧视图名称 | `PVS_BOM` |
| NewViewName | 新视图名称 | `PVS_BOM2` |
| PageSize | 每页拉取行数 | 500 |
| AutoRunEnabled | 是否启用夜间自动执行 | false |
| AutoRunTime | 夜间自动执行时间 | 00:00 |
| SQLiteDbPath | SQLite 数据库文件路径 | BomDiffData.db |
| LogLevel | 日志级别 | Information |
| LogRetentionDays | 日志保留天数 | 30 |

## 核心流程

```
Oracle 分页拉取 → SQLite 批量写入 → Dictionary Diff 对比 → 结果展示
```

### 性能设计

- 分页读取，不一次性加载全量数据
- SQLite 批量事务写入
- Dictionary O(1) 时间复杂度对比
- 单次内存占用 < 500MB，支持百万级数据处理
- 夜间自动执行，避免影响生产库

## Oracle 视图结构

视图名：`PVS_BOM`（旧）、`PVS_BOM2`（新）

| 字段 | 说明 |
|------|------|
| 父项图号 | 父物料编码 |
| 父项名称 | 父物料名称 |
| 父项源 | 父物料来源 |
| 子项图号 | 子物料编码 |
| 子项名称 | 子物料名称 |
| 子项源 | 子物料来源 |
| 数量 | 用量 |

## SQLite 本地存储表

### BOM_SNAPSHOT（快照表）

存储从 Oracle 拉取的视图快照数据。

### BOM_DIFF（差异表）

存储新旧视图对比的差异结果，包含差异类型（新增/删除/变更）及数量变化。
