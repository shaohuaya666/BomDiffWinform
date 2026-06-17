# BOM视图数据对比工具设计方案（WinForm + Oracle + SQLite）

---

# 1. 项目背景

在PLM（Windchill）系统中，BOM视图数据存在以下问题：

- 数据量大（10万～百万级）
- 视图逻辑复杂（版本控制、来源、结构）
- SQL优化后存在数据一致性风险
- 需要对新旧视图进行全量对比验证

---

# 2. 项目目标

## 2.1 功能目标

- Oracle视图分页抽取
- 本地SQLite持久化存储
- 新旧视图全量对比
- 差异数据可视化
- 支持取消与断点续跑
- 夜间自动批处理执行

---

## 2.2 性能目标

- 不一次性加载全量数据
- 单次内存占用 < 500MB
- 支持百万级数据处理
- 夜间自动执行不影响生产库

---

# 3. 系统架构

WinForm UI -> Data Service -> Oracle View -> SQLite

---

# 4. 数据库设计（SQLite）

CREATE TABLE BOM_SNAPSHOT (
    ID INTEGER PRIMARY KEY AUTOINCREMENT,
    SNAPSHOT_TYPE TEXT,
    父项图号 TEXT,
    子项图号 TEXT,
    数量 REAL,
    SOURCE TEXT
);

CREATE TABLE BOM_DIFF (
    ID INTEGER PRIMARY KEY AUTOINCREMENT,
    父项图号 TEXT,
    子项图号 TEXT,
    DIFF_TYPE TEXT,
    OLD_QTY REAL,
    NEW_QTY REAL
);

---

# 5. Oracle分页拉取方案

SELECT *
FROM (
    SELECT t.*, ROW_NUMBER() OVER (ORDER BY 父项图号, 子项图号) rn
    FROM PVS_BOM t
)
WHERE rn BETWEEN :startRow AND :endRow;

---

# 6. 夜间12点自动批处理设计（可配置文件配置）

00:00 启动任务
→ 分页拉取OLD
→ 写SQLite
→ 分页拉取NEW
→ 写SQLite
→ Diff计算

---

# 7. WinForm UI设计

- ProgressBar
- Label状态
- 开始/取消按钮

---

# 8. 核心流程

Oracle分页 → SQLite存储 → Dictionary Diff → 结果展示

---

# 9. 性能优化

- 分页读取
- 批量事务写入SQLite
- Dictionary O(1)对比

# 9. 原视图名与字段（Oracle）

视图名：PVS_BOM（旧），PVS_BOM2（新）
字段:(
    父项图号,
    父项名称,
    父项源,
    子项图号,
    子项名称,
    子项源,
    数量
)
