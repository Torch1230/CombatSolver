#requires -Version 7.4
param([Parameter(Mandatory)][string]$SessionPath)
$ErrorActionPreference = 'Stop'
$session = [IO.Path]::GetFullPath($SessionPath)
$statePath = Join-Path $session 'monitor-state.json'
$stopPath = Join-Path $session 'monitor.stop'
$readyPath = Join-Path $session 'monitor-ready.json'
try {
    Add-Type -AssemblyName PresentationFramework
    Add-Type -AssemblyName PresentationCore
    Add-Type -AssemblyName WindowsBase

    $window = [System.Windows.Window]::new()
    $window.Title = "CombatSolver · $([IO.Path]::GetFileName($session))"
    $window.Width = 620
    $window.Height = 540
    $window.MinWidth = 480
    $window.MinHeight = 380
    $window.Background = [System.Windows.Media.Brushes]::Black
    $window.Foreground = [System.Windows.Media.Brushes]::White
    $window.WindowStartupLocation = [System.Windows.WindowStartupLocation]::CenterScreen

    $scroll = [System.Windows.Controls.ScrollViewer]::new()
    $scroll.VerticalScrollBarVisibility = [System.Windows.Controls.ScrollBarVisibility]::Auto
    $text = [System.Windows.Controls.TextBlock]::new()
    $text.Margin = [System.Windows.Thickness]::new(22)
    $text.FontFamily = [System.Windows.Media.FontFamily]::new('Consolas')
    $text.FontSize = 16
    $text.LineHeight = 27
    $text.TextWrapping = [System.Windows.TextWrapping]::Wrap
    $text.Text = '等待会话状态…'
    $scroll.Content = $text
    $window.Content = $scroll

    function Value([object]$node, [string]$key, [string]$fallback = '—') {
        if ($null -eq $node -or -not $node.Contains($key) -or $null -eq $node[$key] -or "$($node[$key])" -eq '') {
            return $fallback
        }
        return [string]$node[$key]
    }
    function ShortHash([object]$node, [string]$key) {
        $value = Value $node $key
        if ($value.Length -gt 12) { return $value.Substring(0, 12) }
        return $value
    }
    function Refresh {
        if (Test-Path -LiteralPath $stopPath -PathType Leaf) {
            $window.Close()
            return
        }
        if (-not (Test-Path -LiteralPath $statePath -PathType Leaf)) { return }
        $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json -AsHashtable
        $elapsed = if ($null -ne $state.elapsedMilliseconds) { '{0:N1} 秒' -f ([double]$state.elapsedMilliseconds / 1000) } else { '—' }
        $remaining = if ($null -ne $state.remainingMilliseconds) { '{0:N1} 秒' -f ([double]$state.remainingMilliseconds / 1000) } else { '—' }
        $rate = if ($null -ne $state.worldlinesPerSecond) { '{0:N0} 条/秒' -f [double]$state.worldlinesPerSecond } else { '—' }
        $reason = Value $state 'reason'
        if ($reason.Length -gt 400) { $reason = $reason.Substring(0, 400) + '…' }
        $text.Text = @"
CombatSolver  策略会话监控

包            $(Value $state 'reportId')
请求          $(Value $state 'runId')
状态          $(Value $state 'state')
阶段          $(Value $state 'phase')
游戏 PID      $(Value $state 'processId')
脚本版本      $(ShortHash $state 'scriptHash')
参数版本      $(ShortHash $state 'parametersHash')

搜索配置      $(Value $state 'performancePreset')  /  $(Value $state 'parallelism') 并行
请求时限      $(Value $state 'requestTimeoutSeconds') 秒
已用          $elapsed
剩余          $remaining
展开节点      $(Value $state 'expandedNodes')
已查世界线    $(Value $state 'reviewedWorldlines')
搜索速率      $rate
前沿节点      $(Value $state 'frontierNodes')

当前最好战损  $(Value $state 'bestProjectedHpLoss')
当前用药      $(Value $state 'bestPotionCount')
结果或错误    $reason

更新于        $(Value $state 'updatedUtc')

关闭此窗口不会停止 headless 搜索。
"@
    }
    $timer = [System.Windows.Threading.DispatcherTimer]::new()
    $timer.Interval = [TimeSpan]::FromSeconds(1)
    $timer.Add_Tick({ Refresh })
    $window.Add_Loaded({
        Refresh
        @{ pid = $PID; readyUtc = [DateTimeOffset]::UtcNow.ToString('O') } |
            ConvertTo-Json | Set-Content -LiteralPath $readyPath -Encoding utf8
        $timer.Start()
    })
    $window.Add_Closed({ $timer.Stop() })
    $application = [System.Windows.Application]::new()
    [void]$application.Run($window)
} catch {
    ($_ | Out-String) | Set-Content -LiteralPath (Join-Path $session 'monitor-error.log') -Encoding utf8
    throw
}
