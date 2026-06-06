````
# SlotWeave Launcher 架构改进 — 修正实施方案

基于后端审查，以下是修正后的完整实施方案。所有标注问题已修复，可以直接执行。

---

## Phase 1: 关键 Bug 修复（1-2 天）

### 1.1 Result 模式（去除 ComponentState 耦合）

```csharp
// Models/Result.cs
public readonly struct Result
{
    public bool IsSuccess { get; }
    public string? Error { get; }
    
    public static Result Success() => new(true, null);
    public static Result Failure(string error) => new(false, error);
    
    private Result(bool success, string? error)
    {
        IsSuccess = success;
        Error = error;
    }
}

public readonly struct Result<T>
{
    public bool IsSuccess { get; }
    public T? Value { get; }
    public string? Error { get; }
    
    public static Result<T> Success(T value) => new(true, value, null);
    public static Result<T> Failure(string error) => new(false, default, error);
    
    private Result(bool success, T? value, string? error)
    {
        IsSuccess = success;
        Value = value;
        Error = error;
    }
}

// Pipeline 返回包含已执行步骤的结果（用于调用者决定回滚策略）
public readonly struct PipelineResult
{
    public bool IsSuccess { get; }
    public string? Error { get; }
    public IReadOnlyList<IOperationStep> ExecutedSteps { get; }
    
    public static PipelineResult Success(IReadOnlyList<IOperationStep> executedSteps) 
        => new(true, null, executedSteps);
    
    public static PipelineResult Failure(string error, IReadOnlyList<IOperationStep> executedSteps) 
        => new(false, error, executedSteps);
    
    private PipelineResult(bool success, string? error, IReadOnlyList<IOperationStep> executedSteps)
    {
        IsSuccess = success;
        Error = error;
        ExecutedSteps = executedSteps;
    }
}
```

---

### 1.2 完整性验证层

```csharp
// Services/Verification/IIntegrityVerifier.cs
public interface IIntegrityVerifier
{
    Task<Result<bool>> VerifyAsync(string filePath, string? expectedValue = null);
}

// Services/Verification/Sha256Verifier.cs
using System.Security.Cryptography;

public class Sha256Verifier : IIntegrityVerifier
{
    public async Task<Result<bool>> VerifyAsync(string filePath, string? expectedHash = null)
    {
        if (!File.Exists(filePath))
            return Result<bool>.Failure($"文件不存在: {filePath}");
        
        try
        {
            using var sha256 = SHA256.Create();
            await using var stream = File.OpenRead(filePath);
            var hashBytes = await sha256.ComputeHashAsync(stream);
            var hash = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
            
            if (expectedHash == null)
                return Result<bool>.Success(true);
            
            var expected = expectedHash.ToLowerInvariant();
            return hash == expected
                ? Result<bool>.Success(true)
                : Result<bool>.Failure($"SHA256 不匹配.\n期望: {expected}\n实际: {hash}");
        }
        catch (Exception ex)
        {
            return Result<bool>.Failure($"哈希计算失败: {ex.Message}");
        }
    }
}

// Services/Verification/PeHeaderVerifier.cs
public class PeHeaderVerifier : IIntegrityVerifier
{
    private const int MinPeFileSize = 512;  // PE 最小大小（含 DOS stub）
    
    public Task<Result<bool>> VerifyAsync(string filePath, string? _ = null)
    {
        if (!File.Exists(filePath))
            return Task.FromResult(Result<bool>.Failure($"文件不存在: {filePath}"));
        
        try
        {
            var fileInfo = new FileInfo(filePath);
            
            // 基础大小检查：截断的下载不会有完整 PE 结构
            if (fileInfo.Length < MinPeFileSize)
                return Task.FromResult(Result<bool>.Failure(
                    $"文件大小异常: {fileInfo.Length} 字节 (最小 {MinPeFileSize})"));
            
            using var fs = File.OpenRead(filePath);
            
            // 1. 检查 MZ header (DOS stub)
            var mzHeader = new byte[2];
            if (fs.Read(mzHeader, 0, 2) != 2 || mzHeader[0] != 0x4D || mzHeader[1] != 0x5A)
                return Task.FromResult(Result<bool>.Failure("不是有效的 PE 文件 (缺少 MZ 头)"));
            
            // 2. 读取 PE header 偏移 (e_lfanew at offset 0x3C)
            fs.Seek(0x3C, SeekOrigin.Begin);
            var peOffsetBytes = new byte[4];
            if (fs.Read(peOffsetBytes, 0, 4) != 4)
                return Task.FromResult(Result<bool>.Failure("无法读取 PE 偏移"));
            
            var peOffset = BitConverter.ToInt32(peOffsetBytes, 0);
            if (peOffset < 0 || peOffset > fileInfo.Length - 4)
                return Task.FromResult(Result<bool>.Failure($"PE 偏移无效: {peOffset}"));
            
            // 3. 验证 PE signature
            fs.Seek(peOffset, SeekOrigin.Begin);
            var peSig = new byte[4];
            if (fs.Read(peSig, 0, 4) != 4)
                return Task.FromResult(Result<bool>.Failure("无法读取 PE 签名"));
            
            if (peSig[0] != 'P' || peSig[1] != 'E' || peSig[2] != 0 || peSig[3] != 0)
                return Task.FromResult(Result<bool>.Failure("PE 签名无效"));
            
            return Task.FromResult(Result<bool>.Success(true));
        }
        catch (Exception ex)
        {
            return Task.FromResult(Result<bool>.Failure($"PE 验证失败: {ex.Message}"));
        }
    }
}

// Services/Verification/FileSizeVerifier.cs
public class FileSizeVerifier : IIntegrityVerifier
{
    public Task<Result<bool>> VerifyAsync(string filePath, string? expectedSizeStr = null)
    {
        if (!File.Exists(filePath))
            return Task.FromResult(Result<bool>.Failure($"文件不存在: {filePath}"));
        
        var fileInfo = new FileInfo(filePath);
        
        if (expectedSizeStr == null)
            return Task.FromResult(Result<bool>.Success(true));
        
        if (!long.TryParse(expectedSizeStr, out var expectedSize))
            return Task.FromResult(Result<bool>.Failure($"无效的期望大小: {expectedSizeStr}"));
        
        return fileInfo.Length == expectedSize
            ? Task.FromResult(Result<bool>.Success(true))
            : Task.FromResult(Result<bool>.Failure(
                $"文件大小不匹配.\n期望: {expectedSize:N0} 字节\n实际: {fileInfo.Length:N0} 字节"));
    }
}
```

---

### 1.3 配置两阶段提交（修复引用 bug）

```csharp
// Services/ConfigTransaction.cs
using System.Text.Json;

public class ConfigTransaction
{
    private readonly string _path;
    private readonly LauncherConfig _original;  // 原始快照
    private readonly LauncherConfig _modified;  // 修改副本
    private bool _committed;
    
    public ConfigTransaction(string path, LauncherConfig config)
    {
        _path = path;
        
        // 深拷贝原始状态（用于 rollback）
        var json = JsonSerializer.Serialize(config);
        _original = JsonSerializer.Deserialize<LauncherConfig>(json)!;
        
        // 深拷贝工作副本（用于修改）
        _modified = JsonSerializer.Deserialize<LauncherConfig>(json)!;
    }
    
    /// <summary>
    /// 在事务中修改配置（修改的是 _modified 副本）
    /// </summary>
    public void Update(Action<LauncherConfig> modifier)
    {
        if (_committed)
            throw new InvalidOperationException("事务已提交，无法再修改");
        
        modifier(_modified);
    }
    
    /// <summary>
    /// 提交事务：将 _modified 写入文件
    /// </summary>
    public Result Commit()
    {
        if (_committed)
            return Result.Failure("事务已提交");
        
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
            return Result.Failure($"配置保存失败: {ex.Message}");
        }
    }
    
    /// <summary>
    /// 回滚事务：将 _original 写回文件（真正的文件级回滚）
    /// 用于当操作失败需要恢复配置到操作前状态时
    /// </summary>
    public Result Rollback()
    {
        if (_committed)
            return Result.Failure("事务已提交，无法回滚");
        
        try
        {
            var json = JsonSerializer.Serialize(_original, new JsonSerializerOptions 
            { 
                WriteIndented = true 
            });
            
            File.WriteAllText(_path, json);
            _committed = true;  // 标记为已完成（不能再修改）
            
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure($"配置回滚失败: {ex.Message}");
        }
    }
    
    /// <summary>
    /// 放弃事务：不写文件，丢弃所有修改
    /// 用于当操作取消或失败，但不需要回写原始配置时
    /// </summary>
    public void Abort()
    {
        _committed = true;  // 防止意外提交
    }
}
```

---

### 1.4 VerifyCriticalFiles 阻塞化

修改 `Installer.cs` 使关键文件验证失败时触发回滚：

```csharp
// Services/Installer.cs (修改现有方法)
private Result VerifyCriticalFiles(ComponentDefinition definition)
{
    var missingFiles = new List<string>();
    
    foreach (var file in definition.GameFiles)
    {
        var path = Path.Combine(_gameDir, file);
        if (!File.Exists(path))
            missingFiles.Add(file);
    }
    
    if (missingFiles.Count == 0)
        return Result.Success();
    
    var error = $"关键文件缺失 ({missingFiles.Count}):\n" + 
                string.Join("\n", missingFiles.Select(f => $"  • {f}"));
    
    return Result.Failure(error);
}

// 在 InstallOrUpdateAsync 中使用（第 134 行附近）
if (definition.IsCore)
    WriteEmbeddedWinmmDll();

var verifyResult = VerifyCriticalFiles(definition);
if (!verifyResult.IsSuccess)
{
    ConsoleUI.ShowError(verifyResult.Error!);
    ConsoleUI.ShowWarning(isUpdate ? "正在恢复备份..." : "正在清理...");
    
    if (isUpdate)
        RestoreBackup(definition);
    
    CleanupTemp(tempDir);
    return false;  // ← 返回失败而非继续
}

// ... success path
```

---

## Phase 2: 状态管理改进（2-3 天）

### 2.1 ComponentState 枚举（补全转换表）

```csharp
// Models/ComponentState.cs
public enum ComponentState
{
    NotInstalled,
    Installing,
    Installed,
    UpdateAvailable,
    Updating,
    PartialInstall,    // 文件缺失
    Corrupted,         // 文件在但哈希错误
    Uninstalling
}

// Models/InstalledComponent.cs
public class InstalledComponent
{
    private ComponentState _state;
    
    public ComponentState State 
    { 
        get => _state;
        private set => _state = value;  // 只能通过 TransitionTo 或 SetStateFromScan 修改
    }
    
    public ComponentDefinition Definition { get; }
    public string? InstalledVersion { get; private set; }
    public string? LatestVersion { get; internal set; }
    public bool LatestVersionCheckFailed { get; internal set; }
    
    public InstalledComponent(ComponentDefinition definition)
    {
        Definition = definition;
        _state = ComponentState.NotInstalled;
    }
    
    /// <summary>
    /// 状态转换（带验证）— 用于操作流程中的状态变更
    /// </summary>
    public Result TransitionTo(ComponentState newState)
    {
        if (!IsValidTransition(_state, newState))
            return Result.Failure(
                $"非法状态转换: {Definition.Name} [{_state} → {newState}]");
        
        _state = newState;
        return Result.Success();
    }
    
    /// <summary>
    /// 直接设置状态（绕过转换验证）— 仅供扫描器使用
    /// 扫描器是根据文件系统状态推断的，不受操作状态机约束
    /// </summary>
    internal void SetStateFromScan(ComponentState scannedState)
    {
        _state = scannedState;
    }
    
    private static bool IsValidTransition(ComponentState from, ComponentState to)
    {
        return (from, to) switch
        {
            // 安装流程
            (ComponentState.NotInstalled, ComponentState.Installing) => true,
            (ComponentState.Installing, ComponentState.Installed) => true,
            (ComponentState.Installing, ComponentState.PartialInstall) => true,
            (ComponentState.Installing, ComponentState.NotInstalled) => true,  // 回滚
            
            // 更新流程
            (ComponentState.Installed, ComponentState.Updating) => true,
            (ComponentState.UpdateAvailable, ComponentState.Updating) => true,
            (ComponentState.Updating, ComponentState.Installed) => true,
            (ComponentState.Updating, ComponentState.Corrupted) => true,
            (ComponentState.Updating, ComponentState.UpdateAvailable) => true,  // 回滚失败
            
            // 卸载流程
            (ComponentState.Installed, ComponentState.Uninstalling) => true,
            (ComponentState.UpdateAvailable, ComponentState.Uninstalling) => true,
            (ComponentState.PartialInstall, ComponentState.Uninstalling) => true,
            (ComponentState.Corrupted, ComponentState.Uninstalling) => true,
            (ComponentState.Uninstalling, ComponentState.NotInstalled) => true,
            
            // 修复流程
            (ComponentState.PartialInstall, ComponentState.Installing) => true,
            (ComponentState.Corrupted, ComponentState.Installing) => true,
            
            // 版本检查导致的状态变更
            (ComponentState.Installed, ComponentState.UpdateAvailable) => true,
            (ComponentState.UpdateAvailable, ComponentState.Installed) => true,  // 检查后发现是最新
            
            // 扫描发现状态变化（这些由 SetStateFromScan 处理，但列出来文档化）
            // (any, ComponentState.NotInstalled) => via SetStateFromScan
            // (any, ComponentState.PartialInstall) => via SetStateFromScan
            // (any, ComponentState.Corrupted) => via SetStateFromScan
            
            _ => false
        };
    }
    
    public void UpdateInstalledVersion(string? version)
    {
        InstalledVersion = version;
    }
    
    public bool NeedsAction => _state is 
        ComponentState.UpdateAvailable or 
        ComponentState.PartialInstall or 
        ComponentState.Corrupted;
    
    public string StatusIcon => _state switch
    {
        ComponentState.NotInstalled => "○",
        ComponentState.Installed => "✓",
        ComponentState.UpdateAvailable => "⚠",
        ComponentState.PartialInstall => "⚠",
        ComponentState.Corrupted => "✗",
        ComponentState.Installing => "⋯",
        ComponentState.Updating => "⋯",
        ComponentState.Uninstalling => "⋯",
        _ => "?"
    };
    
    public string StateDescription => _state switch
    {
        ComponentState.NotInstalled => "未安装",
        ComponentState.Installing => "安装中...",
        ComponentState.Installed => "已安装",
        ComponentState.UpdateAvailable => "有更新",
        ComponentState.Updating => "更新中...",
        ComponentState.PartialInstall => "不完整",
        ComponentState.Corrupted => "已损坏",
        ComponentState.Uninstalling => "卸载中...",
        _ => "未知"
    };
}
```

---

### 2.2 ComponentScanner 单组件扫描

```csharp
// Services/ComponentScanner.cs
public class ComponentScanner
{
    /// <summary>
    /// 扫描单个组件（优化：只读取目标组件的文件）
    /// </summary>
    public InstalledComponent ScanOne(ComponentDefinition definition, string gameDir)
    {
        var component = new InstalledComponent(definition);
        
        var installDir = Path.Combine(gameDir, definition.InstallPath);
        var installDirExists = Directory.Exists(installDir);
        
        var gameFiles = definition.GameFiles
            .Select(f => Path.Combine(gameDir, f))
            .ToList();
        
        var existingFiles = gameFiles.Where(File.Exists).ToList();
        
        // 状态判定逻辑
        ComponentState scannedState;
        
        if (!installDirExists && existingFiles.Count == 0)
        {
            // 完全未安装
            scannedState = ComponentState.NotInstalled;
        }
        else if (installDirExists && existingFiles.Count == gameFiles.Count)
        {
            // 目录存在且所有文件都在 → 已安装（可能需要更新，由版本检查决定）
            scannedState = ComponentState.Installed;
        }
        else
        {
            // 部分文件存在 → 不完整安装
            // 注意：当前不做哈希验证（Corrupted 状态留给安装后验证步骤）
            scannedState = ComponentState.PartialInstall;
        }
        
        component.SetStateFromScan(scannedState);
        
        // 读取已安装版本（如果有文件存在）
        if (scannedState != ComponentState.NotInstalled)
        {
            var version = ReadInstalledVersion(definition, gameDir);
            component.UpdateInstalledVersion(version);
        }
        
        return component;
    }
    
    /// <summary>
    /// 扫描所有组件
    /// </summary>
    public List<InstalledComponent> ScanAll(
        IEnumerable<ComponentDefinition> definitions, 
        string gameDir)
    {
        return definitions
            .Select(def => ScanOne(def, gameDir))
            .ToList();
    }
    
    private string? ReadInstalledVersion(ComponentDefinition definition, string gameDir)
    {
        // 现有逻辑保持不变
        // ...
    }
}
```

---

### 2.3 版本检查失败标志

修改 `MenuController.cs` 的 `CheckForUpdatesAsync`:

```csharp
// UI/MenuController.cs
private async Task CheckForUpdatesAsync()
{
    ConsoleUI.ShowStatus("版本检查", "检查组件更新...");
    
    var tasks = _components.Select(async component =>
    {
        var versionResult = await _githubService.GetLatestVersionAsync(
            component.Definition.Repo);
        
        if (!versionResult.IsSuccess)
        {
            component.LatestVersionCheckFailed = true;
            component.LatestVersion = null;
            ConsoleUI.ShowWarning(
                $"[{component.Definition.Name}] 版本检查失败: {versionResult.Error}");
            return;
        }
        
        component.LatestVersionCheckFailed = false;
        component.LatestVersion = versionResult.Value;
        
        // 更新状态：如果当前是 Installed 且有新版本，转为 UpdateAvailable
        if (component.State == ComponentState.Installed 
            && component.LatestVersion != null 
            && component.InstalledVersion != component.LatestVersion)
        {
            component.TransitionTo(ComponentState.UpdateAvailable);
        }
        else if (component.State == ComponentState.UpdateAvailable 
                 && component.LatestVersion == component.InstalledVersion)
        {
            // 检查后发现已是最新版（可能 GitHub 回滚了 release）
            component.TransitionTo(ComponentState.Installed);
        }
    });
    
    await Task.WhenAll(tasks);
    
    // 统计
    var updateCount = _components.Count(c => c.State == ComponentState.UpdateAvailable);
    var partialCount = _components.Count(c => c.State == ComponentState.PartialInstall);
    var corruptedCount = _components.Count(c => c.State == ComponentState.Corrupted);
    var checkFailedCount = _components.Count(c => c.LatestVersionCheckFailed);
    
    if (checkFailedCount > 0)
        ConsoleUI.ShowWarning($"{checkFailedCount} 个组件版本检查失败（可能是网络问题）");
    
    if (updateCount > 0)
        ConsoleUI.ShowInfo($"发现 {updateCount} 个可更新组件");
    
    if (partialCount > 0)
        ConsoleUI.ShowWarning($"{partialCount} 个组件安装不完整");
    
    if (corruptedCount > 0)
        ConsoleUI.ShowError($"{corruptedCount} 个组件已损坏");
}
```

更新状态显示（`ConsoleUI.cs`）：

```csharp
public static void DisplayComponentStatus(InstalledComponent component)
{
    var icon = component.StatusIcon;
    var name = component.Definition.Name;
    var state = component.StateDescription;
    
    // 版本信息
    string versionInfo;
    if (component.LatestVersionCheckFailed)
    {
        versionInfo = component.InstalledVersion != null
            ? $"{component.InstalledVersion} (无法检查更新)"
            : "(无法检查版本)";
    }
    else if (component.State == ComponentState.NotInstalled)
    {
        versionInfo = component.LatestVersion != null
            ? $"最新: {component.LatestVersion}"
            : "(未知版本)";
    }
    else if (component.State == ComponentState.UpdateAvailable)
    {
        versionInfo = $"{component.InstalledVersion} → {component.LatestVersion}";
    }
    else
    {
        versionInfo = component.InstalledVersion ?? "(未知版本)";
        if (component.LatestVersion == component.InstalledVersion)
            versionInfo += " (最新)";
    }
    
    Console.WriteLine($"  {icon} {name,-25} {state,-10} {versionInfo}");
}
```

---

## Phase 3: 架构升级（3-5 天）

### 3.1 操作管道（修正版）

```csharp
// Services/Pipeline/IOperationStep.cs
public interface IOperationStep
{
    string Name { get; }
    
    /// <summary>
    /// 检查前置条件（失败时不需要回滚）
    /// </summary>
    Result CanExecute();
    
    /// <summary>
    /// 执行步骤（可能有副作用）
    /// </summary>
    Task<Result> ExecuteAsync(CancellationToken ct = default);
    
    /// <summary>
    /// 回滚已执行的副作用
    /// </summary>
    Task<Result> RollbackAsync();
}

// Services/Pipeline/OperationPipeline.cs
public class OperationPipeline
{
    private readonly List<IOperationStep> _steps = new();
    private readonly Stack<IOperationStep> _executedSteps = new();
    
    public OperationPipeline AddStep(IOperationStep step)
    {
        _steps.Add(step);
        return this;
    }
    
    public async Task<PipelineResult> ExecuteAsync(CancellationToken ct = default)
    {
        _executedSteps.Clear();  // ← 修正：清理上次执行残留
        
        foreach (var step in _steps)
        {
            ct.ThrowIfCancellationRequested();
            
            // 前置检查（失败时不回滚）
            var canExecute = step.CanExecute();
            if (!canExecute.IsSuccess)
            {
                ConsoleUI.ShowError($"前置条件检查失败 [{step.Name}]: {canExecute.Error}");
                return PipelineResult.Failure(canExecute.Error!, _executedSteps.ToList());
            }
            
            // 执行
            ConsoleUI.ShowInfo($"[{step.Name}]");  // ← 修正：使用 ShowInfo
            
            var result = await step.ExecuteAsync(ct);
            
            if (!result.IsSuccess)
            {
                ConsoleUI.ShowError($"步骤失败 [{step.Name}]: {result.Error}");
                await RollbackAsync();
                return PipelineResult.Failure(result.Error!, _executedSteps.ToList());
            }
            
            _executedSteps.Push(step);
        }
        
        return PipelineResult.Success(_executedSteps.ToList());
    }
    
    private async Task RollbackAsync()
    {
        if (_executedSteps.Count == 0)
            return;
        
        ConsoleUI.ShowWarning($"正在回滚 {_executedSteps.Count} 个已执行步骤...");
        
        while (_executedSteps.TryPop(out var step))
        {
            try
            {
                var rollback = await step.RollbackAsync();
                if (!rollback.IsSuccess)
                    ConsoleUI.ShowError($"回滚失败 [{step.Name}]: {rollback.Error}");
                else
                    ConsoleUI.ShowInfo($"已回滚 [{step.Name}]");
            }
            catch (Exception ex)
            {
                ConsoleUI.ShowError($"回滚异常 [{step.Name}]: {ex.Message}");
            }
        }
    }
}
```

---

### 3.2 具体步骤实现示例

```csharp
// Services/Pipeline/Steps/DownloadAssetStep.cs
public class DownloadAssetStep : IOperationStep
{
    private readonly GitHubRelease _release;
    private readonly ComponentDefinition _definition;
    private readonly string _targetDir;
    private readonly IAssetDownloader _downloader;
    private string? _downloadedFilePath;
    
    public string Name => $"下载 {_definition.Name} v{_release.TagName}";
    
    public Result CanExecute()
    {
        if (!Directory.Exists(_targetDir))
        {
            try
            {
                Directory.CreateDirectory(_targetDir);
            }
            catch (Exception ex)
            {
                return Result.Failure($"无法创建临时目录: {ex.Message}");
            }
        }
        
        return Result.Success();
    }
    
    public async Task<Result> ExecuteAsync(CancellationToken ct = default)
    {
        var downloadResult = await _downloader.DownloadAsync(
            _definition, 
            _release.TagName, 
            _targetDir,
            ct);
        
        if (!downloadResult.IsSuccess)
            return Result.Failure(downloadResult.Error!);
        
        _downloadedFilePath = downloadResult.Value;
        return Result.Success();
    }
    
    public Task<Result> RollbackAsync()
    {
        if (_downloadedFilePath != null && File.Exists(_downloadedFilePath))
        {
            try
            {
                File.Delete(_downloadedFilePath);
            }
            catch (Exception ex)
            {
                return Task.FromResult(Result.Failure($"无法删除下载文件: {ex.Message}"));
            }
        }
        
        return Task.FromResult(Result.Success());
    }
}

// Services/Pipeline/Steps/VerifyDownloadStep.cs
public class VerifyDownloadStep : IOperationStep
{
    private readonly string _filePath;
    private readonly IIntegrityVerifier _verifier;
    private readonly string? _expectedValue;
    
    public string Name => "验证下载文件";
    
    public VerifyDownloadStep(
        string filePath, 
        IIntegrityVerifier verifier, 
        string? expectedValue = null)
    {
        _filePath = filePath;
        _verifier = verifier;
        _expectedValue = expectedValue;
    }
    
    public Result CanExecute()
    {
        return File.Exists(_filePath)
            ? Result.Success()
            : Result.Failure($"文件不存在: {_filePath}");
    }
    
    public async Task<Result> ExecuteAsync(CancellationToken ct = default)
    {
        var result = await _verifier.VerifyAsync(_filePath, _expectedValue);
        return result.IsSuccess
            ? Result.Success()
            : Result.Failure(result.Error!);
    }

    public Task<Result> RollbackAsync()
    {
        // Verification has no side effects to roll back
        return Task.FromResult(Result.Success());
    }
}
````

---

## Phase 4: 集成到现有代码（1-2 天）

### 4.1 SelfUpdater 接入验证层

```csharp
// Services/SelfUpdater.cs — UpdateAsync 中下载后加入验证
// 在 DownloadAssetAsync 成功后、TrySaveConfigVersion 之前:

// Step 1.5 — Verify downloaded exe before swapping
var peVerifier = new PeHeaderVerifier();
var peResult = await peVerifier.VerifyAsync(newExe);
if (!peResult.IsSuccess)
{
    ConsoleUI.ShowError($"Download verification failed: {peResult.Error}");
    TryDelete(newExe);
    return false;
}

// If GitHub provided a digest, also verify SHA256
if (!string.IsNullOrEmpty(_assetDigest))
{
    var shaVerifier = new Sha256Verifier();
    var shaResult = await shaVerifier.VerifyAsync(newExe, _assetDigest);
    if (!shaResult.IsSuccess)
    {
        ConsoleUI.ShowError($"Hash verification failed: {shaResult.Error}");
        TryDelete(newExe);
        return false;
    }
}
```

### 4.2 SelfUpdater 接入配置事务

```csharp
// Services/SelfUpdater.cs — 替换 TrySaveConfigVersion()
// 在 PE 验证通过后、swap 之前:

var configPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "SlotWeave.Launcher", "launcher_config.json");

var tx = new ConfigTransaction(configPath, _config);
tx.Update(c => c.LauncherVersion = _latestVersion!);

// … rename swap …

if (swapSucceeded)
{
    tx.Commit();  // ← 只有 swap 成功才写配置
}
else
{
    tx.Abort();
}
```

### 4.3 实施优先级

| 步骤 | 内容 | 预计时间 |
|------|------|---------|
| 4.3a | SelfUpdater 接入 PeHeaderVerifier | 30 分钟 |
| 4.3b | SelfUpdater 接入 ConfigTransaction | 30 分钟 |
| 4.3c | 集成测试（模拟截断下载、swap 失败） | 1 小时 |
| 4.3d | 清理 backward-compat 布尔属性 | 2 小时 | NEW:  src/Models/Result.cs  src/Models/ComponentState.cs  src/Models/InstalledComponent.cs  src/Services/Verification/IIntegrityVerifier.cs  src/Services/Verification/PeHeaderVerifier.cs  src/Services/Verification/Sha256Verifier.cs  src/Services/Verification/FileSizeVerifier.cs  src/Services/ConfigTransaction.cs  src/Services/IOperationStep.csMODIFIED:  src/Models/ComponentDefinition.cs   — removed old InstalledComponent class  src/Services/Installer.cs           — VerifyCriticalFiles returns Result (blocking)  src/Services/ComponentScanner.cs    — ScanOne + ScanAll refactor  src/UI/MenuController.cs            — RefreshComponentStateAsync + CheckForUpdatesAsyncUNCHANGED (not yet wired):  src/Services/SelfUpdater.cs         — PeHeaderVerifier + ConfigTransaction pending  src/Services/GitHubService.cs       — interface split pending (Phase 3)
````