param(
    [Parameter(Mandatory=$true)][int]$TargetPid,
    [Parameter(Mandatory=$true)][string]$DllDir,
    [Parameter(Mandatory=$true)][string]$KeyPath,
    [Parameter(Mandatory=$true)][string]$LogPath,
    [int]$Seconds = 180
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $DllDir
Remove-Item -LiteralPath $KeyPath -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $LogPath -Force -ErrorAction SilentlyContinue
$dllPath = Join-Path $DllDir 'wx_key.dll'
$dllImportPath = $dllPath.Replace('\', '\\')
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class WxKeyNative {
    [DllImport("$dllImportPath", CallingConvention = CallingConvention.Cdecl)]
    public static extern bool InitializeHook(UInt32 targetPid);
    [DllImport("$dllImportPath", CallingConvention = CallingConvention.Cdecl)]
    public static extern bool PollKeyData(StringBuilder keyBuffer, int bufferSize);
    [DllImport("$dllImportPath", CallingConvention = CallingConvention.Cdecl)]
    public static extern bool GetStatusMessage(StringBuilder statusBuffer, int bufferSize, out int level);
    [DllImport("$dllImportPath", CallingConvention = CallingConvention.Cdecl)]
    public static extern bool CleanupHook();
    [DllImport("$dllImportPath", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr GetLastErrorMsg();
}
"@
function Write-Log([string]$line) {
    Add-Content -LiteralPath $LogPath -Value $line -Encoding UTF8
}
Write-Log "START pid=$TargetPid seconds=$Seconds dll=$dllPath"
$initialized = [WxKeyNative]::InitializeHook([uint32]$TargetPid)
if (-not $initialized) {
    $ptr = [WxKeyNative]::GetLastErrorMsg()
    $msg = if ($ptr -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::PtrToStringUTF8($ptr) } else { 'unknown' }
    Write-Log "INIT_FAILED $msg"
    exit 2
}
Write-Log "INIT_OK"
$keyBuf = New-Object System.Text.StringBuilder 128
$logBuf = New-Object System.Text.StringBuilder 1024
$deadline = (Get-Date).AddSeconds($Seconds)
try {
    while ((Get-Date) -lt $deadline) {
        $null = $keyBuf.Clear()
        if ([WxKeyNative]::PollKeyData($keyBuf, $keyBuf.Capacity)) {
            $key = $keyBuf.ToString()
            if ($key -match '^[0-9a-fA-F]{64}$') {
                Set-Content -LiteralPath $KeyPath -Value ("DB Key: " + $key) -Encoding ASCII
                Write-Log "KEY_FOUND"
                exit 0
            }
            Write-Log "KEY_INVALID_LENGTH"
        }
        do {
            $null = $logBuf.Clear()
            $level = 0
            $hasLog = [WxKeyNative]::GetStatusMessage($logBuf, $logBuf.Capacity, [ref]$level)
            if ($hasLog) {
                $line = $logBuf.ToString() -replace '(?<![0-9a-fA-F])[0-9a-fA-F]{64}(?![0-9a-fA-F])','<REDACTED_KEY>'
                Write-Log ("LOG L$level " + $line)
            }
        } while ($hasLog)
        Start-Sleep -Milliseconds 100
    }
    Write-Log "TIMEOUT"
    exit 3
}
finally {
    [void][WxKeyNative]::CleanupHook()
    Write-Log "CLEANUP"
}
