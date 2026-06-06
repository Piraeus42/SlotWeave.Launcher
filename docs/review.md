好的，我来做一次全面的架构审查。这个文档写得很详实，暴露了不少问题。我会从架构层面给出改进方案，而不仅仅是修bug。

---

## 架构审查报告

### 当前架构的核心问题

1. **状态管理分散且脆弱** — `InstalledComponent` 用多个布尔标志表示状态，存在不可能状态（`IsInstalled=false && IsPartial=true` 时 `HasUpdate` 逻辑错误）
2. **操作结果与状态刷新脱节** — 操作成功才刷新状态，失败时状态变成僵尸
3. **缺乏操作原子性保证** — 自更新、安装都有"写了配置但操作失败"的窗口期
4. **错误处理策略不一致** — 有的地方返回 `false`，有的返回 `true` 但打印警告
5. **GitHub 交互层职责混乱** — Atom feed 用于版本检查，REST API 用于下载，但调用时机散落在各处

---

## 改进方案

### 1. 引入显式状态机（ComponentState）

用枚举替换布尔标志组合：

```csharp
public enum ComponentState
{
    NotInstalled,
    Installing,        // 新增：正在安装
    Installed,
    UpdateAvailable,   // LatestVersion > InstalledVersion
    Updating,          // 新增：正在更新
    PartialInstall,    // 安装目录存在但文件缺失
    Corrupted,         // 新增：安装目录和文件都存在，但校验失败
    Uninstalling       // 新增：正在卸载
}

public class InstalledComponent
{
    public ComponentState State { get; private set; }
    public string? InstalledVersion { get; private set; }
    public string? LatestVersion { get; internal set; }  // 只能由 VersionChecker 设置
    
    // 状态转换方法（带验证）
    public Result TransitionTo(ComponentState newState)
    {
        if (!IsValidTransition(State, newState))
            return Result.Failure($"Invalid state transition: {State} -> {newState}");
        
        State = newState;
        return Result.Success();
    }
    
    private static bool IsValidTransition(ComponentState from, ComponentState to)
    {
        return (from, to) switch
        {
            (NotInstalled, Installing) => true,
            (Installing, Installed) => true,
            (Installing, PartialInstall) => true,
            (Installing, NotInstalled) => true,  // 回滚
            (Installed, Updating) => true,
            (Installed, Uninstalling) => true,
            (UpdateAvailable, Updating) => true,
            (Updating, Installed) => true,
            (Updating, Corrupted) => true,
            (Updating, UpdateAvailable) => true,  // 回滚
            (PartialInstall, Installing) => true,  // 修复性安装
            (PartialInstall, Uninstalling) => true,
            (_, NotInstalled) when to == NotInstalled => true,  // 卸载成功
            _ => false
        };
    }
    
    public bool NeedsAction => State is ComponentState.UpdateAvailable 
                                     or ComponentState.PartialInstall 
                                     or ComponentState.Corrupted;
    
    public string StatusIcon => State switch
    {
        ComponentState.NotInstalled => "○",
        ComponentState.Installed => "✓",
        ComponentState.UpdateAvailable => "⚠",
        ComponentState.PartialInstall => "⚠",
        ComponentState.Corrupted => "✗",
        ComponentState.Installing or ComponentState.Updating or ComponentState.Uninstalling => "⋯",
        _ => "?"
    };
}
```

**收益**：
- 不可能的状态变成编译期错误
- 状态转换有明确的规则和验证
- 中间状态（Installing/Updating）可以阻止并发操作

---

### 2. 统一操作结果模型（Result Pattern）

```csharp
public readonly struct Result
{
    public bool IsSuccess { get; }
    public string? Error { get; }
    public ComponentState? RollbackState { get; }  // 失败时应回滚到的状态
    
    public static Result Success() => new(true, null, null);
    public static Result Failure(string error, ComponentState? rollbackTo = null) 
        => new(false, error, rollbackTo);
    
    private Result(bool success, string? error, ComponentState? rollback)
    {
        IsSuccess = success;
        Error = error;
        RollbackState = rollback;
    }
}

public readonly struct Result<T>
{
    public bool IsSuccess { get; }
    public T? Value { get; }
    public string? Error { get; }
    
    public static Result<T> Success(T value) => new(true, value, null);
    public static Result<T> Failure(string error) => new(false, default, error);
    
    // 支持链式调用
    public Result<U> Map<U>(Func<T, U> mapper)
        => IsSuccess ? Result<U>.Success(mapper(Value!)) : Result<U>.Failure(Error!);
}
```

**收益**：
- 强制调用者处理错误
- `RollbackState` 明确了失败后应该回到哪个状态
- 消除了"返回 true 但打印警告"的模糊行为

---

### 3. 操作管道（Operation Pipeline）

把安装/更新/卸载抽象为一系列步骤，每步都有：
- 前置条件检查
- 执行逻辑
- 成功时的副作用
- 失败时的回滚

```csharp
public interface IOperationStep
{
    string Name { get; }
    Result CanExecute();  // 前置条件
    Task<Result> ExecuteAsync();  // 执行
    Task<Result> RollbackAsync();  // 回滚
}

public class OperationPipeline
{
    private readonly List<IOperationStep> _steps = new();
    private readonly Stack<IOperationStep> _executedSteps = new();
    
    public OperationPipeline AddStep(IOperationStep step)
    {
        _steps.Add(step);
        return this;
    }
    
    public async Task<Result> ExecuteAsync()
    {
        foreach (var step in _steps)
        {
            var canExecute = step.CanExecute();
            if (!canExecute.IsSuccess)
                return await RollbackAsync(canExecute.Error!);
            
            ConsoleUI.ShowStatus($"[{step.Name}]");
            var result = await step.ExecuteAsync();
            
            if (!result.IsSuccess)
                return await RollbackAsync(result.Error!);
            
            _executedSteps.Push(step);
        }
        
        return Result.Success();
    }
    
    private async Task<Result> RollbackAsync(string error)
    {
        ConsoleUI.ShowWarning($"操作失败: {error}，正在回滚...");
        
        while (_executedSteps.TryPop(out var step))
        {
            var rollback = await step.RollbackAsync();
            if (!rollback.IsSuccess)
                ConsoleUI.ShowError($"回滚步骤 [{step.Name}] 失败: {rollback.Error}");
        }
        
        return Result.Failure(error);
    }
}

// 使用示例
public async Task<Result> InstallOrUpdateAsync(InstalledComponent component, GitHubRelease release)
{
    var originalState = component.State;
    var targetState = component.State == ComponentState.NotInstalled 
        ? ComponentState.Installing 
        : ComponentState.Updating;
    
    component.TransitionTo(targetState);
    
    var pipeline = new OperationPipeline()
        .AddStep(new CheckGameProcessStep())
        .AddStep(new DownloadAssetStep(release, _tempDir))
        .AddStep(new VerifyDownloadStep(_tempDir))
        .AddStep(new CreateBackupStep(component, _backupDir))
        .AddStep(new ExtractAndCopyStep(component, _tempDir))
        .AddStep(new VerifyCriticalFilesStep(component))  // 阻塞性验证
        .AddStep(new WriteVersionMarkerStep(component, release.TagName))
        .AddStep(new CleanupTempStep(_tempDir));
    
    var result = await pipeline.ExecuteAsync();
    
    if (result.IsSuccess)
    {
        component.TransitionTo(ComponentState.Installed);
        component.UpdateInstalledVersion(release.TagName);
    }
    else
    {
        component.TransitionTo(result.RollbackState ?? originalState);
    }
    
    return result;
}
```

**收益**：
- 每个步骤职责单一，可测试
- 自动回滚链保证了操作的原子性
- 状态转换在操作前后明确发生
- `VerifyCriticalFilesStep` 如果失败会触发回滚，不会再报"成功"

---

### 4. 完整性验证层（Integrity Verification）

```csharp
public interface IIntegrityVerifier
{
    Task<Result<bool>> VerifyAsync(string filePath, string? expectedHash = null);
}

public class Sha256Verifier : IIntegrityVerifier
{
    public async Task<Result<bool>> VerifyAsync(string filePath, string? expectedHash = null)
    {
        if (!File.Exists(filePath))
            return Result<bool>.Failure($"File not found: {filePath}");
        
        using var sha256 = SHA256.Create();
        await using var stream = File.OpenRead(filePath);
        var hash = BitConverter.ToString(await sha256.ComputeHashAsync(stream))
                               .Replace("-", "").ToLowerInvariant();
        
        if (expectedHash == null)
            return Result<bool>.Success(true);  // 只检查文件存在
        
        return hash == expectedHash.ToLowerInvariant()
            ? Result<bool>.Success(true)
            : Result<bool>.Failure($"Hash mismatch. Expected: {expectedHash}, Got: {hash}");
    }
}

public class PeHeaderVerifier : IIntegrityVerifier
{
    public Task<Result<bool>> VerifyAsync(string filePath, string? expectedHash = null)
    {
        if (!File.Exists(filePath))
            return Task.FromResult(Result<bool>.Failure($"File not found: {filePath}"));
        
        try
        {
            using var fs = File.OpenRead(filePath);
            var header = new byte[2];
            fs.Read(header, 0, 2);
            
            if (header[0] != 0x4D || header[1] != 0x5A)  // "MZ"
                return Task.FromResult(Result<bool>.Failure("Invalid PE header"));
            
            return Task.FromResult(Result<bool>.Success(true));
        }
        catch (Exception ex)
        {
            return Task.FromResult(Result<bool>.Failure($"PE verification failed: {ex.Message}"));
        }
    }
}

// 在 DownloadAssetStep 中使用
public class VerifyDownloadStep : IOperationStep
{
    private readonly string _filePath;
    private readonly string? _expectedHash;
    private readonly IIntegrityVerifier _verifier;
    
    public string Name => "验证下载文件";
    
    public Result CanExecute() => Result.Success();
    
    public async Task<Result> ExecuteAsync()
    {
        var result = await _verifier.VerifyAsync(_filePath, _expectedHash);
        return result.IsSuccess 
            ? Result.Success() 
            : Result.Failure(result.Error!);
    }
    
    public Task<Result> RollbackAsync()
    {
        if (File.Exists(_filePath))
            File.Delete(_filePath);
        return Task.FromResult(Result.Success());
    }
}
```

**收益**：
- 修复 S1（自更新零校验）和 I4（组件下载零校验）
- 可扩展：可以加 ZIP CRC 验证、文件大小验证等
- 验证失败会触发回滚，删除损坏文件

---

### 5. 配置管理改进（两阶段提交）

```csharp
public class ConfigTransaction
{
    private readonly string _path;
    private readonly LauncherConfig _original;
    private LauncherConfig _modified;
    private bool _committed;
    
    public ConfigTransaction(string path, LauncherConfig config)
    {
        _path = path;
        _original = JsonSerializer.Deserialize<LauncherConfig>(
            JsonSerializer.Serialize(config))!;  // 深拷贝
        _modified = config;
    }
    
    public void Update(Action<LauncherConfig> modifier)
    {
        if (_committed)
            throw new InvalidOperationException("Transaction already committed");
        modifier(_modified);
    }
    
    public Result Commit()
    {
        if (_committed)
            return Result.Failure("Already committed");
        
        try
        {
            var json = JsonSerializer.Serialize(_modified, new JsonSerializerOptions 
            { 
                WriteIndented = true 
            });
            File.WriteAllText(_path, json);
            _committed = true;
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure($"Config save failed: {ex.Message}");
        }
    }
    
    public void Rollback()
    {
        // 恢复到原始配置（内存中）
        JsonSerializer.Serialize(_original);  // 不实际写文件
    }
}

// 在自更新中使用
public async Task<Result> UpdateAsync()
{
    var transaction = new ConfigTransaction(_configPath, _config);
    
    var pipeline = new OperationPipeline()
        .AddStep(new ResolveDownloadUrlStep(this))
        .AddStep(new DownloadLauncherStep(this, _downloadUrl))
        .AddStep(new VerifyLauncherStep(_newExePath))  // ← 新增 PE + SHA256 验证
        .AddStep(new UpdateConfigVersionStep(transaction, _latestVersion))  // ← 写配置但不提交
        .AddStep(new SwapExecutableStep(_currentExePath, _newExePath));
    
    var result = await pipeline.ExecuteAsync();
    
    if (result.IsSuccess)
    {
        transaction.Commit();  // ← 只有交换成功才提交配置
        Process.Start(_currentExePath);
        Environment.Exit(0);
    }
    else
    {
        transaction.Rollback();
    }
    
    return result;
}
```

**收益**：
- 修复 S2（配置在交换前就写入）
- 配置和文件操作要么都成功，要么都回滚
- 事务模式可以扩展到其他需要原子性的操作

---

### 6. GitHub 服务层重构

```csharp
public interface IVersionChecker
{
    Task<Result<string?>> GetLatestVersionAsync(ComponentDefinition component);
}

public interface IAssetDownloader
{
    Task<Result<string>> DownloadAsync(ComponentDefinition component, string version, string targetDir);
}

public class AtomVersionChecker : IVersionChecker
{
    // 专注于版本检查，无状态
    public async Task<Result<string?>> GetLatestVersionAsync(ComponentDefinition component)
    {
        try
        {
            // ... Atom feed 逻辑
            return Result<string?>.Success(version);
        }
        catch (Exception ex)
        {
            return Result<string?>.Failure($"Version check failed: {ex.Message}");
        }
    }
}

public class GitHubAssetDownloader : IAssetDownloader
{
    private readonly IIntegrityVerifier _verifier;
    
    public async Task<Result<string>> DownloadAsync(ComponentDefinition component, string version, string targetDir)
    {
        // 1. 调用 REST API 获取 release
        var releaseResult = await GetReleaseAsync(component.Repo, version);
        if (!releaseResult.IsSuccess)
            return Result<string>.Failure(releaseResult.Error!);
        
        var release = releaseResult.Value!;
        
        // 2. 找到匹配的 asset
        var asset = FindMatchingAsset(release, component);
        if (asset == null)
            return Result<string>.Failure($"No matching asset in release {version}");
        
        // 3. 下载
        var filePath = Path.Combine(targetDir, asset.Name);
        var downloadResult = await DownloadFileAsync(asset.BrowserDownloadUrl, filePath);
        if (!downloadResult.IsSuccess)
            return downloadResult;
        
        // 4. 验证（如果 GitHub 提供了 digest）
        if (!string.IsNullOrEmpty(asset.Digest))
        {
            var verifyResult = await _verifier.VerifyAsync(filePath, asset.Digest);
            if (!verifyResult.IsSuccess)
            {
                File.Delete(filePath);
                return Result<string>.Failure(verifyResult.Error!);
            }
        }
        
        return Result<string>.Success(filePath);
    }
}

// 在 MenuController 中使用
public class MenuController
{
    private readonly IVersionChecker _versionChecker;
    private readonly IAssetDownloader _assetDownloader;
    
    public async Task CheckForUpdatesAsync()
    {
        foreach (var component in _components)
        {
            var versionResult = await _versionChecker.GetLatestVersionAsync(component.Definition);
            
            if (!versionResult.IsSuccess)
            {
                component.LatestVersionCheckFailed = true;  // ← 新增标志
                ConsoleUI.ShowWarning($"[{component.Definition.Name}] {versionResult.Error}");
                continue;
            }
            
            component.LatestVersion = versionResult.Value;
            component.LatestVersionCheckFailed = false;
            
            // 根据版本更新状态
            if (component.State == ComponentState.Installed 
                && component.LatestVersion != null 
                && component.InstalledVersion != component.LatestVersion)
            {
                component.TransitionTo(ComponentState.UpdateAvailable);
            }
        }
    }
}
```

**收益**：
- 修复 G1（Atom 失败静默）— 现在有 `LatestVersionCheckFailed` 标志
- 职责分离：版本检查和下载不再耦合
- 下载步骤集成了完整性验证
- 接口化便于测试和 mock

---

### 7. 组件扫描优化

```csharp
public class ComponentScanner
{
    // 新增：单个扫描
    public InstalledComponent ScanOne(ComponentDefinition definition, string gameDir)
    {
        var component = new InstalledComponent(definition);
        
        var installDir = Path.Combine(gameDir, definition.InstallPath);
        var installDirExists = Directory.Exists(installDir);
        
        var gameFiles = definition.GameFiles
            .Select(f => Path.Combine(gameDir, f))
            .ToList();
        
        var existingFiles = gameFiles.Where(File.Exists).ToList();
        
        // 状态判定
        if (installDirExists && existingFiles.Count == gameFiles.Count)
        {
            component.State = ComponentState.Installed;
        }
        else if (existingFiles.Count > 0 || installDirExists)
        {
            component.State = ComponentState.PartialInstall;
        }
        else
        {
            component.State = ComponentState.NotInstalled;
        }
        
        // 读取版本（如果已安装）
        if (component.State != ComponentState.NotInstalled)
        {
            component.UpdateInstalledVersion(ReadInstalledVersion(definition, gameDir));
        }
        
        return component;
    }
    
    public List<InstalledComponent> ScanAll(IEnumerable<ComponentDefinition> definitions, string gameDir)
    {
        return definitions.Select(def => ScanOne(def, gameDir)).ToList();
    }
}

// 在 RefreshComponentStateAsync 中使用
private async Task RefreshComponentStateAsync(InstalledComponent component)
{
    await Task.Run(() =>
    {
        var fresh = _scanner.ScanOne(component.Definition, _gameDir!);  // ← 只扫一个
        
        // 保留运行时状态（如 LatestVersion, LatestVersionCheckFailed）
        component.State = fresh.State;
        component.UpdateInstalledVersion(fresh.InstalledVersion);
    });
}
```

**收益**：
- 修复 B3（全量扫描浪费）
- `RefreshComponentStateAsync` 从 O(N²) 降到 O(N)

---

### 8. 依赖排序（Launcher 优先更新）

```csharp
public class UpdateOrchestrator
{
    private readonly SelfUpdater _selfUpdater;
    private readonly List<InstalledComponent> _components;
    
    public async Task<Result> CheckAndPromptUpdatesAsync()
    {
        var launcherNeedsUpdate = await _selfUpdater.CheckAsync();
        var componentsNeedUpdate = _components.Any(c => c.NeedsAction);
        
        if (!launcherNeedsUpdate && !componentsNeedUpdate)
        {
            ConsoleUI.ShowSuccess("所有组件均为最新");
            return Result.Success();
        }
        
        if (launcherNeedsUpdate)
        {
            ConsoleUI.ShowWarning("检测到启动器更新。建议先更新启动器再更新组件。");
            Console.Write("是否立即更新启动器? [Y/n] ");
            
            if (Console.ReadLine()?.Trim().ToLower() != "n")
            {
                var result = await _selfUpdater.UpdateAsync();
                if (result.IsSuccess)
                {
                    ConsoleUI.ShowSuccess("启动器将重启以完成更新");
                    // 自动退出，新版本启动
                }
                else
                {
                    ConsoleUI.ShowError($"启动器更新失败: {result.Error}");
                    Console.Write("是否继续更新组件? [y/N] ");
                    if (Console.ReadLine()?.Trim().ToLower() != "y")
                        return result;
                }
            }
        }
        
        // 继续更新组件...
        return Result.Success();
    }
}
```

**收益**：
- 修复缺失的依赖排序（推荐 6）
- 用户明确知道更新顺序

---

## 实施优先级

### Phase 1（修复关键 bug，1-2 天）
1. ✅ 引入 `Result` 模型
2. ✅ `VerifyCriticalFilesStep` 阻塞化
3. ✅ 自更新 PE + SHA256 验证
4. ✅ 配置两阶段提交

### Phase 2（改进状态管理，2-3 天）
5. ✅ `ComponentState` 枚举 + 状态转换验证
6. ✅ `ScanOne` 单组件扫描
7. ✅ `LatestVersionCheckFailed` 标志

### Phase 3（架构升级，3-5 天）
8. ✅ `OperationPipeline` + 各步骤分解
9. ✅ GitHub 服务层分离（`IVersionChecker`, `IAssetDownloader`）
10. ✅ `UpdateOrchestrator` 依赖排序

### Phase 4（可选优化）
- 后台版本检查（避免启动时阻塞）
- 配置文件版本迁移机制
- 日志框架（替换 `ConsoleUI.ShowError`）
- 单元测试覆盖核心逻辑

---

## 风险评估

| 改动              | 风险 | 缓解措施                             |
| ----------------- | ---- | ------------------------------------ |
| 状态机重构        | 高   | 先在副本上实现，通过集成测试验证     |
| `Result` 模型引入 | 中   | 逐步迁移，旧代码可以共存             |
| 操作管道          | 中   | 先在 Installer 中试点，成功后推广    |
| GitHub 服务层分离 | 低   | 接口化，原实现作为默认实现，改动量小 |
| 配置事务          | 低   | 只改配置写入逻辑，读取逻辑不变       |

---

需要我开始实施这些改进吗？我建议从 **Phase 1** 开始，先修复那几个关键 bug，然后逐步重构。想先改哪个部分？

---

## 实施审查：后端核实

> 以下是对方案各模块的代码级验证。每个 "✅" 表示方案可直接实施，"⚠️" 表示有实施陷阱需要修正。

### §1 ComponentState 枚举

**状态区分度**: ✅ 清楚。`Corrupted`（文件全在但内容损坏）vs `PartialInstall`（文件缺失）的区分有意义——前者是哈希校验失败的产物，后者是 AV 删文件或安装中断的产物。

**缺失的合法转换**: ⚠️ 转换表缺少以下路径，实践中会发生：

| 缺失转换 | 触发场景 |
|---------|---------|
| `NotInstalled → PartialInstall` | 安装到一半进程被杀，下次扫描时目录在但文件不全 |
| `PartialInstall → NotInstalled` | 用户手动删掉了残留的部分文件 |
| `Corrupted → NotInstalled` | 用户手动清理损坏的安装后重新扫描 |
| `Installed → Corrupted` | 启动时完整性扫描发现哈希不匹配 |

`ScanComponent` 是纯函数（输入文件系统，输出状态），不经过 `TransitionTo()`。这意味着扫描结果需要绕过转换表直接设置状态。需要加一个 `SetStateFromScan(ComponentState)` 旁路方法，文档化"仅扫描器使用"。

**与现有字段的映射**: 

```
现有                              → 新
IsInstalled=false, IsPartial=false → NotInstalled
IsInstalled=true,  IsPartial=false, HasUpdate=false → Installed
IsInstalled=true,  IsPartial=false, HasUpdate=true  → UpdateAvailable
IsInstalled=true,  IsPartial=true                   → PartialInstall
-                                                   → Corrupted (新增)
```

`HasUpdate` 被 `State == UpdateAvailable` 替代。`NeedsAction` 被 `State is UpdateAvailable or PartialInstall or Corrupted` 替代。注意现有代码中 `MenuController.cs:177` 用 `HasUpdate || IsPartial` 过滤更新菜单——等价转换是 `State is UpdateAvailable or PartialInstall or Corrupted`。

**实施判断**: ✅ 方向正确，需要补 4 个转换 + 1 个扫描器旁路。

---

### §2 Result 模式

**`RollbackState` 耦合**: ⚠️ `Result` 里放 `ComponentState?` 把通用结果类型绑定到了组件状态机。这个 `Result` 也会用于自更新（`SwapExecutableStep` 失败回滚）、下载验证、配置事务——这些都不涉及 `ComponentState`。

修正方案：`RollbackState` 不放在 `Result` 里，而是放在 `IOperationStep` 里：

```csharp
public interface IOperationStep
{
    string Name { get; }
    Result CanExecute();
    Task<Result> ExecuteAsync();
    Task<Result> RollbackAsync();
    object? RollbackContext { get; }  // 由 pipeline 传递给调用者
}
```

或者更简单——pipeline 的 `ExecuteAsync` 返回一个 `PipelineResult` 包含已执行步骤列表，调用者自行决定回滚目标状态：

```csharp
public readonly struct PipelineResult
{
    public bool IsSuccess { get; }
    public string? Error { get; }
    public IReadOnlyList<IOperationStep> ExecutedSteps { get; }  // 用于回滚
}
```

**`Result<T>.Map` 方法**: ✅ 有用但不是必须。当前项目没有链式 Result 的需求，可以 Phase 3 再加。

**实施判断**: ⚠️ 移除 `RollbackState`，或从 `Result` 移到操作层。

---

### §3 操作管道

**`CanExecute` 失败时触发回滚**: ⚠️ 

```csharp
var canExecute = step.CanExecute();
if (!canExecute.IsSuccess)
    return await RollbackAsync(canExecute.Error!);  // ← 此时 _executedSteps 为空
```

回滚循环遍历空栈不会有副作用，但 `RollbackAsync` 会打印 "正在回滚..."——这对 `CanExecute` 失败的情况是误导。修正：

```csharp
if (!canExecute.IsSuccess)
{
    // No steps executed yet, no rollback needed
    ConsoleUI.ShowError($"前置条件检查失败 [{step.Name}]: {canExecute.Error}");
    return canExecute;
}
```

**`ConsoleUI.ShowStatus` 调用签名不匹配**: ⚠️

```csharp
ConsoleUI.ShowStatus($"[{step.Name}]");
```

`ConsoleUI.ShowStatus(string label, string value, bool isWarning = false)` 需要两个必填参数。这里只传了一个。修正：

```csharp
ConsoleUI.ShowInfo($"[{step.Name}]");  // 或
ConsoleUI.ShowStatus(step.Name, "执行中...");
```

**`_executedSteps` 残留**: ⚠️ pipeline 被复用时（`InstallAllAsync` 场景），上次执行的 `_executedSteps` 栈里还有数据。需要在 `ExecuteAsync` 开头 `_executedSteps.Clear()`。

**无 CancellationToken**: ⚠️ 现有 `Installer.InstallOrUpdateAsync` 接受 `CancellationToken ct`，Pipeline 丢失了这个能力。`ExecuteAsync` 需要加 `CancellationToken` 参数并传给每一步。

**`CheckGameProcessStep` 的交互逻辑**: 现有 `CheckAndCloseGame()` 会轮询等待用户关闭游戏，内部有 `Console.ReadKey`。作为 Pipeline Step 这没问题——Step 内部可以有自己的交互——但 Step 的 `Name` 应该提示用户"等待关闭游戏"而非静默。

**实施判断**: ⚠️ 4 处修正后可用。

---

### §4 完整性验证

**PE 验证深度**: ⚠️ `PeHeaderVerifier` 只检查了 `MZ` 前两个字节。DOS 时代的 16 位 exe 也有 `MZ` 头。完整的 PE 验证应该：

```csharp
// 1. MZ magic (offset 0)
fs.Seek(0, SeekOrigin.Begin);
if (header[0] != 0x4D || header[1] != 0x5A) return fail;

// 2. Read e_lfanew at offset 0x3C
fs.Seek(0x3C, SeekOrigin.Begin);
var peOffsetBytes = new byte[4];
fs.Read(peOffsetBytes, 0, 4);
var peOffset = BitConverter.ToInt32(peOffsetBytes, 0);

// 3. PE signature at e_lfanew
fs.Seek(peOffset, SeekOrigin.Begin);
var peSig = new byte[4];
fs.Read(peSig, 0, 4);
if (peSig[0] != 'P' || peSig[1] != 'E' || peSig[2] != 0 || peSig[3] != 0) return fail;
```

不过对于 .NET single-file exe，`MZ` 检查在生产环境中足够（一个截断到 100 字节的文件不会有完整的 `MZ` + `e_lfanew`）。如果追求严谨，加完整检查；如果追求简洁，加个注释说明"仅 MZ 已覆盖截断场景"。

**GitHub `asset.digest` 可用性**: ⚠️ 需验证。GitHub REST API v3 的 asset 对象确实有 `digest` 字段（RFC 3230 实例摘要），但**不是所有 asset 都有**——取决于 GitHub 生成时的条件。当前代码里 `GitHubAsset.Digest` 标记为 `string?`，说明可能是 null。**不能说"如果 GitHub 提供了 digest"就验证——必须在 Phase 1 先验证 `Piraeus42/SlotWeave.Launcher` 的 release asset 是否实际返回了 digest 值。** 如果没有，SHA256 验证退化为 PE 头验证 + 文件大小检查。

**验证步骤的 `Rollback`**: ✅ `VerifyDownloadStep.RollbackAsync()` 删除损坏文件是正确的——失败时清理 `.new` 或 `.zip`。

**实施判断**: ⚠️ 先确认 GitHub digest 可用性，PE 检查至少 MZ + 文件大小。

---

### §5 配置两阶段提交 (ConfigTransaction)

**`_modified = config` 是引用赋值**: ❌ **严重 bug**。

```csharp
public ConfigTransaction(string path, LauncherConfig config)
{
    _original = JsonSerializer.Deserialize<LauncherConfig>(
        JsonSerializer.Serialize(config))!;  // 深拷贝 ✅
    _modified = config;  // ← 这是引用！修改 _modified 会污染外部的 config
}
```

修正：

```csharp
_modified = JsonSerializer.Deserialize<LauncherConfig>(
    JsonSerializer.Serialize(config))!;  // 也深拷贝
```

并且 `Update()` 方法修改 `_modified`，`Commit()` 时把 `_modified` 写盘 + 把 `_modified` 的属性拷回原始 `config` 引用（因为调用者持有的是原始引用的指针）。

**`Rollback()` 是空操作**: ❌

```csharp
public void Rollback()
{
    JsonSerializer.Serialize(_original);  // 序列化到一个丢弃的 string
}
```

这行代码除了浪费 CPU 什么都没做。如果交易的生命周期是：创建 → 修改 → 提交/丢弃，那么 Rollback 的语义是"丢弃修改，不写文件"。只需要：

```csharp
public void Rollback()
{
    // Transaction aborted — _modified changes are discarded.
    // _original is preserved for reference but no file I/O is performed.
    _committed = true;  // 防止意外 Commit
}
```

或者如果需要"写回原始值到文件"（真正的文件级回滚）：

```csharp
public void Rollback()
{
    var json = JsonSerializer.Serialize(_original, new JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(_path, json);
}
```

**方案中的使用方式有问题**: 方案 §5 的使用示例中，`UpdateConfigVersionStep` 修改了 transaction，但 swap 在它*之后*才执行。如果 swap 失败，pipeline 调用 `transaction.Rollback()`——但 swap 失败意味着 exe 文件没变，配置文件也没被 commit，所以什么都不需要做。但如果 swap 失败发生在配置已经 commit 之后......这正是当前 S2 的 bug。方案正确地解决了：commit 挪到 swap 成功之后。

**实施判断**: ❌→⚠️ 两处 bug 必须修。修复后可用。

---

### §6 GitHub 服务层重构

**接口分离**: ✅ `IVersionChecker` + `IAssetDownloader` 切得干净。当前 `GitHubService` 已经同时承担这两个职责，拆分是纯重构，无行为变化。

**`LatestVersionCheckFailed` 标志**: ✅ 新字段需要加在 `InstalledComponent` 上。值为 true 时 `StatusIcon` 应显示 `⚠`（不一定是 `✗`），因为可能是网络问题而非文件损坏。

**`FindMatchingAsset` 的 `.exe` / `.zip` 回退顺序**: G3 问题在方案中没有被修复。接口分离后，`IAssetDownloader` 的实现仍然需要处理 asset 匹配失败的情况。建议在 `FindMatchingAsset` 中增加一个 `contentType` 偏好参数：

```csharp
public GitHubAsset? FindMatchingAsset(GitHubRelease release, string assetPattern, string? preferredExtension = null)
{
    // ... existing logic ...
    // Fallback: prefer the preferred extension
    if (preferredExtension != null)
    {
        var byExt = release.Assets.FirstOrDefault(a => 
            a.Name.EndsWith(preferredExtension, StringComparison.OrdinalIgnoreCase));
        if (byExt != null) return byExt;
    }
    // ... existing .exe /.zip fallback ...
}
```

**实施判断**: ✅ 加 `LatestVersionCheckFailed` 字段和一个扩展名偏好参数。

---

### §7 组件扫描优化 (ScanOne)

**缺失 GameFiles 回退**: ⚠️ 方案中 `ScanOne` 的逻辑：

```csharp
if (installDirExists && existingFiles.Count == gameFiles.Count)
    component.State = ComponentState.Installed;
else if (existingFiles.Count > 0 || installDirExists)
    component.State = ComponentState.PartialInstall;
```

但现有 `ScanComponent` 分两支：installDir 存在时用 `result.IsInstalled = true` + 逐个 GameFiles 验证；installDir 不存在时用 GameFiles 回退检查。方案中的逻辑把这两支合并了，但缺少对 `!installDirExists && fileChecks.All(x => x)` 场景的处理——当**没有主安装目录但所有散落文件都存在**时（例如只有 `winmm.dll`），现有代码设 `IsInstalled = true`（虽然 installDir 不存在但进入了"all files present"分支），而方案会判定为 `PartialInstall`。

这个差异需要确认：`SlotWeave.zip` 解压后一定有 `SlotWeave/` 目录吗？如果一定有，那 `!installDirExists` 就是异常状态，判为 `PartialInstall` 是正确的。

**实施判断**: ⚠️ 需要确认 zip 结构。如果核心组件的 zip 一定包含 `SlotWeave/` 目录，则方案逻辑正确。

---

### §8 依赖排序 (UpdateOrchestrator)

**`UpdateAsync()` 不返回**: ❌ 自更新成功后调用 `Environment.Exit(0)`，所以 Orchestrator 中 `UpdateAsync()` 之后的代码永远不会执行。这不是 bug——launcher 更新完必然重启——但方案中的代码暗示它返回了：

```csharp
var result = await _selfUpdater.UpdateAsync();
if (result.IsSuccess)
{
    ConsoleUI.ShowSuccess("启动器将重启以完成更新");
    // 自动退出，新版本启动  ← 永远不会执行到这里
}
```

`UpdateAsync` 成功时已经在内部调用 `Environment.Exit(0)` 了，不会返回。修正：去掉 `if (result.IsSuccess)` 后的代码，直接替换为注释说明"UpdateAsync 成功后不返回"。

或者，从 `UpdateAsync` 中移除 `Environment.Exit(0)`，让调用者决定是否退出。但这会增加复杂度。当前设计（`UpdateAsync` 内部 exit）是合理且安全的——swap 成功后新 exe 已经在跑了，旧进程必须退出。

**实施判断**: ⚠️ 修正 Orchestrator 示例代码，标注 UpdateAsync 成功后不会返回。

---

## Phase 实施修正总表

| 方案模块 | 判定 | 需修正项 |
|---------|------|---------|
| §1 ComponentState | ⚠️ | 补 4 个转换 + ScanOnly 旁路方法 |
| §2 Result | ⚠️ | 移除 RollbackState，或移到 PipelineResult |
| §3 OperationPipeline | ⚠️ | CanExecute 不回滚 + ShowStatus→ShowInfo + Clear executedSteps + CT |
| §4 Integrity | ⚠️ | 先验证 GitHub digest 可用性 + PE 至少 MZ+size |
| §5 ConfigTransaction | ❌→⚠️ | **必修**: `_modified` 深拷贝 + Rollback 实现 |
| §6 GitHub Service | ✅ | 加 LatestVersionCheckFailed + FindMatchingAsset ext 参数 |
| §7 ScanOne | ⚠️ | 确认 zip 结构后决定 |
| §8 Orchestrator | ⚠️ | 标注 UpdateAsync 不返回 |
| Phase 分阶段 | ✅ | 顺序合理，Phase 1 优先修关键 bug |