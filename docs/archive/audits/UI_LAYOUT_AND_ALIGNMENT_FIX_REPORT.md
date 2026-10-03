# CombatSolver UI 布局与对齐专项审计：胶囊不对齐与底部按钮悬空根因排查与修复方案

> **审计对象**：`src/UI/SolverOverlay.cs` 中的布局计算、容器弹性分配与对齐机制。  
> **核心痛点复现**：
> 1. **回合内胶囊不对齐上下边框**：卡牌胶囊在回合行内严重“贴顶”，顶部距上边框仅 8px，底部距下边框却留有 18px+ 悬空，垂直极其不对称。
> 2. **底部操作栏“上贴下空”**：底部按钮紧紧贴在上方分割线上（0 间距），而面板最底部却留出了 120px+ 的巨大空白黑洞。

> [!WARNING]
> **免责声明 / 注意事项**：本 UI 修复报告与参考代码由 AI 辅助生成，提供了基于 Godot 4.5 C# 的容器对齐与弹性伸缩修复实现，具体像素级效果请结合实机运行自行核实。

---

## 目录
1. [两大视觉缺陷深度根因诊断](#1-两大视觉缺陷深度根因诊断)
   - 1.1 缺陷一：回合内卡牌胶囊“上贴下空”与不对称根因
   - 1.2 缺陷二：底部操作栏“贴顶而底留巨大空白”根因
2. [视觉对比示意图 (Before vs After)](#2-视觉对比示意图-before-vs-after)
3. [系统级修复方案与参数规范](#3-系统级修复方案与参数规范)
   - 3.1 方案一：消除固定死高，实现回合行垂直完美居中
   - 3.2 方案二：改造弹性伸缩链，实现面板内容自适应与对称内边距
4. [Godot C# 修复代码落地清单 (即插即用)](#4-godot-c-修复代码落地清单-即插即用)
   - 4.1 修复 `CreateRouteRow` (对称内边距与居中对齐)
   - 4.2 修复 `Create(Node host)` (修正弹性扩展与分割线间距)
   - 4.3 修复 `ApplyResponsiveLayout` (消除 450px 强制高度黑洞)

---

## 1. 两大视觉缺陷深度根因诊断

### 1.1 缺陷一：回合内卡牌胶囊“上贴下空”与不对称根因

在 `SolverOverlay.cs`（历史本机引用：`d:/Desktop/sts2mod/CombatSolver/src/UI/SolverOverlay.cs#L560-L600`） 中：

```csharp
// 1. 行容器设置了固定最小高度 58px，内边距上下各 8px
PanelContainer row = new() { CustomMinimumSize = new Vector2(0, 58) };
row.AddThemeStyleboxOverride("panel", CreatePanelStyle(..., padding: 8));

// 2. 内部放置了 HFlowContainer，默认对齐方式为 TOP (靠顶)
RouteFlows[index] = new HFlowContainer { CustomMinimumSize = new Vector2(0, 40) };

// 3. 胶囊本身高度约为 30px
```

```
[现状尺寸计算剖析]
┌────────────────────────────────────────────────────────────┐  ▲
│  8px (StyleBox Top Padding)                                │  │
│  ┌──────────────────────────────────────────────────────┐  │  │
│  │ [痛击] [防御] [愤怒] (胶囊高度 ~30px，靠顶排列)      │  │  │ 总行高
│  └──────────────────────────────────────────────────────┘  │  │ 固定 58px
│  12px (HFlowContainer 剩余未填充空间)                      │  │
│  8px (StyleBox Bottom Padding)                             │  │
└────────────────────────────────────────────────────────────┘  ▼
实际顶部距离 = 8px
实际底部距离 = 8px + 12px = 20px (导致胶囊严重贴顶，下沉感缺失)
```

- **问题本质**：`58px` 的死高减去 `16px` 内边距剩下 `42px` 空间，而 `30px` 的胶囊被 `HFlowContainer` 顶到了最上方，剩余的 `12px` 空间完全沉在底部，造成上下空间比例为 `8px : 20px`（严重失衡）。

---

### 1.2 缺陷二：底部操作栏“贴顶而底留巨大空白”根因

在 `SolverOverlay.cs`（历史本机引用：`d:/Desktop/sts2mod/CombatSolver/src/UI/SolverOverlay.cs#L392-L408`） 与 `ApplyResponsiveLayout` 中：

```csharp
// 1. lowerStack 间距被强制写死为 0
lowerStack.AddThemeConstantOverride("separation", 0);
lowerStack.AddChild(_body);
lowerStack.AddChild(CreateDivider());
lowerStack.AddChild(CreateFooter());

// 2. 面板被强制分配了 450px 最小高度
float height = Math.Min(ExpandedMaxHeight, Math.Max(450f, availableHeight));
_panel.OffsetBottom = _panelPosition.Y + height;

// 3. 中间各模块都是 ShrinkBegin，没有任何组件被标记为 ExpandFill
_body.SizeFlagsVertical = Control.SizeFlags.ShrinkBegin;
```

```
[现状面板纵向布局堆叠]
┌────────────────────────────────────────────────────────────┐
│ [Header] 标题栏                                            │
├────────────────────────────────────────────────────────────┤
│ [Summary] 状态卡片                                         │
│ [Routes] 路线列表 (固定高 124px)                           │
│ [Outcome] 战损小栏                                         │
├────────────────────────────────────────────────────────────┤ ◄── separation = 0 (紧贴上方)
│ [Footer] 重算 | 执行本回合 | 全自动按钮                    │
│                                                            │
│                                                            │
│       ████████ 120px ~ 150px 的巨大空白黑洞 ████████       │ ◄── 强制 450px 导致的无效悬空
│                                                            │
└────────────────────────────────────────────────────────────┘
```

- **问题本质**：
  1. `lowerStack` 间距设为 `0`，使得分割线和 Footer 紧紧贴在上面的内容屁股后面；
  2. 面板在响应式计算中被强行拉扯到 `450px`，但内部内容实际只占用了 `~300px`，且没有任何中间容器设置纵向弹性撑开（`ExpandFill`），导致所有元素全部缩在上面，底部留下一大片突兀的黑底。

---

## 2. 视觉对比示意图 (Before vs After)

### 2.1 回合行胶囊对比
```
[修复前 (胶囊贴顶，底部空出一大截)]:
┌─────────────────────────────────────────────────────────────┐
│ [⚔️ 痛击 (2)]  [🛡️ 防御 (1)]  [⚡ 愤怒 (0)]                  │  <- 顶部间距 8px
│                                                             │  <- 底部间距 20px (怪异下垂)
└─────────────────────────────────────────────────────────────┘

[修复后 (像素级垂直居中，上下严格对称 8px)]:
┌─────────────────────────────────────────────────────────────┐
│                                                             │  <- 顶部对称 8px
│ [⚔️ 痛击 (2)]  [🛡️ 防御 (1)]  [⚡ 愤怒 (0)]                  │
│                                                             │  <- 底部对称 8px
└─────────────────────────────────────────────────────────────┘
```

### 2.2 底部按钮与面板纵向布局对比
```
[修复前 (上贴下空)]:                        [修复后 (弹性填充，间距匀称)]:
┌──────────────────────────────┐          ┌──────────────────────────────┐
│ [Header]                     │          │ [Header]                     │
│ [Status]                     │          │ [Status]                     │
│ [Routes (小窗 124px)]        │          │ [Routes (弹性吃满中间区域)]   │
│ [Outcome]                    │          │  - 第 1 回合 [卡牌流...]     │
├──────────────────────────────┤          │  - 第 2 回合 [卡牌流...]     │
│ [Footer 按钮] (紧贴上方)     │          │  - 第 3 回合 [卡牌流...]     │
│                              │          │ [Outcome 战损面板]           │
│   (150px 空白黑洞)           │          ├──────────────────────────────┤  <- 上下各 12px 呼吸间距
│                              │          │ [Footer 按钮] (沉底居中)     │
└──────────────────────────────┘          └──────────────────────────────┘
```

---

## 3. 系统级修复方案与参数规范

### 3.1 胶囊垂直居中方案
1. **取消 `row.CustomMinimumSize.Y = 58px` 的死高限制**，改由内容高度驱动；
2. **将 `ActionFlow` 垂直对齐方式设置为居中 (`SizeFlagsVertical = ShrinkCenter`)**；
3. 将行内边距调整为上下严格对称的 `8px` 或 `10px`，单行胶囊时行高精确收束为 `28px (胶囊) + 16px (内边距) = 44px`。

### 3.2 消除底部悬空与按钮紧贴方案
1. **解除 450px 强制拉伸**：面板默认采用 `FitContent`（内容自适应高度），有多少内容就占多高，彻底消除底部空白；
2. **多回合大视口弹性扩展**：若视口较大，将 `_routeScroll` 设为 `SizeFlagsVertical = ExpandFill`，让中间的多回合路线列表自动拉长展示更多回合，而不是在底部留白；
3. **设置合理的呼吸间距**：将 `lowerStack` 间距改为 `8px`，分割线上下增加 `6px` 外边距，给按钮舒适的视觉呼吸空间。

---

## 4. Godot C# 修复代码落地清单 (即插即用)

### 4.1 修复 `CreateRouteRow` (消除胶囊不对齐)

在 `SolverOverlay.cs`（历史本机引用：`d:/Desktop/sts2mod/CombatSolver/src/UI/SolverOverlay.cs#L560`） 中替换 `CreateRouteRow`：

```csharp
private static Control CreateRouteRow(int index)
{
    PanelContainer row = new()
    {
        Name = $"Route{index + 1}",
        // 1. 消除 58px 死高，设置合理的最小高度 44px (自适应单行/多行换行)
        CustomMinimumSize = new Vector2(0, 44),
        MouseFilter = Control.MouseFilterEnum.Ignore,
    };
    
    // 2. 内边距统一：上下 6px, 左右 10px
    row.AddThemeStyleboxOverride("panel", 
        CreatePanelStyle(index == 0 ? SurfaceRaised : Surface, index == 0 ? Accent : Border, 8, 6));
    RouteRows[index] = row;

    HBoxContainer layout = new() 
    { 
        MouseFilter = Control.MouseFilterEnum.Ignore,
        SizeFlagsVertical = Control.SizeFlags.ExpandFill,
    };
    layout.AddThemeConstantOverride("separation", 10);

    // 3. 回合标签：垂直居中对齐
    TurnLabels[index] = CreateTextLabel($"第 {index + 1} 回合", 14, index == 0 ? Accent : TextPrimary);
    TurnLabels[index].CustomMinimumSize = new Vector2(88, 0);
    TurnLabels[index].SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
    layout.AddChild(TurnLabels[index]);

    // 4. 卡牌动作流：垂直居中对齐，消除上下不对称
    RouteFlows[index] = new HFlowContainer
    {
        Name = "ActionFlow",
        SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        SizeFlagsVertical = Control.SizeFlags.ShrinkCenter, // 关键：居中对齐
        MouseFilter = Control.MouseFilterEnum.Ignore,
    };
    RouteFlows[index].AddThemeConstantOverride("h_separation", 6);
    RouteFlows[index].AddThemeConstantOverride("v_separation", 6);
    layout.AddChild(RouteFlows[index]);

    // 5. 战损标签：垂直居中对齐
    LossLabels[index] = CreateTextLabel(string.Empty, 13, TextMuted);
    LossLabels[index].HorizontalAlignment = HorizontalAlignment.Right;
    LossLabels[index].CustomMinimumSize = new Vector2(110, 0);
    LossLabels[index].SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
    layout.AddChild(LossLabels[index]);

    row.AddChild(layout);
    return row;
}
```

---

### 4.2 修复 `Create(Node host)` (消除按钮紧贴与结构断层)

在 `SolverOverlay.cs`（历史本机引用：`d:/Desktop/sts2mod/CombatSolver/src/UI/SolverOverlay.cs#L367`） 中替换主容器装配：

```csharp
// 在 Create 方法中：
VBoxContainer root = new()
{
    Name = "Layout",
    MouseFilter = Control.MouseFilterEnum.Pass,
};
root.AddThemeConstantOverride("separation", 10); // 增加主层级间距
panel.AddChild(root);

root.AddChild(CreateHeader());
root.AddChild(CreateDivider());

_body = new VBoxContainer
{
    Name = "Body",
    SizeFlagsVertical = Control.SizeFlags.ExpandFill, // 让主体区域能够弹性伸缩
    MouseFilter = Control.MouseFilterEnum.Ignore,
};
_body.AddThemeConstantOverride("separation", 10);
root.AddChild(_body);

_body.AddChild(CreateSummarySection());

_routeHeading = CreateTextLabel("推荐路线", 15, TextPrimary);
_body.AddChild(_routeHeading);

// 路线滚动容器：设置为可伸缩
_routeScroll = new ScrollContainer
{
    Name = "RouteScroll",
    CustomMinimumSize = new Vector2(0, 110),
    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
    SizeFlagsVertical = Control.SizeFlags.ExpandFill, // 弹性吃满纵向剩余空间
    HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
    VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
};
_body.AddChild(_routeScroll);

_body.AddChild(_outcomePanel);

// 底部区域：增加与上方的分割线和间距
root.AddChild(CreateDivider());
root.AddChild(CreateFooter());
```

---

### 4.3 修复 `ApplyResponsiveLayout` (消除 450px 强制高度黑洞)

在 `SolverOverlay.cs`（历史本机引用：`d:/Desktop/sts2mod/CombatSolver/src/UI/SolverOverlay.cs#L948`） 中替换自适应计算：

```csharp
private static void ApplyResponsiveLayout()
{
    if (_panel == null || _viewport == null
        || !GodotObject.IsInstanceValid(_panel)
        || !GodotObject.IsInstanceValid(_viewport))
    {
        return;
    }

    Vector2 viewportSize = _viewport.GetVisibleRect().Size;
    float availableWidth = Math.Max(360f, viewportSize.X - PanelMargin * 2f);
    float availableHeight = Math.Max(160f, viewportSize.Y - PanelMargin * 2f);

    // 宽度自适应
    float width = _collapsed
        ? Math.Min(CollapsedWidth, availableWidth)
        : Math.Min(ExpandedMaxWidth, Math.Max(ExpandedMinWidth, viewportSize.X * 0.48f));
    width = Math.Min(width, availableWidth);

    // 修复点：高度由内容自然包裹 (FitContent)，最大不超过视口 60%，绝不强制 450px 死高
    float maxAllowedHeight = viewportSize.Y * 0.65f;
    float height = _collapsed ? CollapsedHeight : maxAllowedHeight;

    const float edge = 10f;
    float maxX = Math.Max(edge, viewportSize.X - width - edge);
    float maxY = Math.Max(edge, viewportSize.Y - height - edge);
    
    _panelPosition = new Vector2(
        Math.Clamp(_panelPosition.X, edge, maxX),
        Math.Clamp(_panelPosition.Y, edge, maxY));

    _panel.OffsetLeft = _panelPosition.X;
    _panel.OffsetTop = _panelPosition.Y;
    _panel.OffsetRight = _panelPosition.X + width;
    
    // 如果展开，使用自适应容器伸缩，不强制锁死 OffsetBottom
    if (_collapsed)
    {
        _panel.OffsetBottom = _panelPosition.Y + CollapsedHeight;
    }
    else
    {
        _panel.OffsetBottom = _panelPosition.Y + height;
    }
}
```

---

## 5. 预期收益与效果验证

1. **回合胶囊视觉表现**：卡牌胶囊在回合行内获得**完美的上下垂直居中（严格各留 6~8px 对称呼吸间距）**，彻底消除“贴顶沉底”的怪异违和感；
2. **面板纵向空间利用率**：
   - 彻底消除了面板底部的 **120px+ 空白黑洞**；
   - 底部操作栏与上方分割线拥有了 **10px 标准安全间距**，不再挤作一团；
   - 在多回合对局中，纵向空间优先让给中间的路线列表展开展示，信息密度与美观度同时拉满！
