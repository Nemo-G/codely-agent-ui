param(
  [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
)

$ErrorActionPreference = 'Stop'

$patterns = @(
  'Packages/**/native/build/**',
  'Packages/**/CMakeFiles/**',
  'Packages/**/*.obj',
  'cn.tuanjie.codely.unity-agent-client-ui/Editor/WindowSync/native/build/**',
  'cn.tuanjie.codely.unity-agent-client-ui/Editor/WindowSync/native/build.meta',
  'cn.tuanjie.codely.unity-agent-client-ui/Editor/WindowSync/native/**/CMakeFiles/**',
  'cn.tuanjie.codely.unity-agent-client-ui/Editor/WindowSync/native/**/*.obj'
)

$violations = New-Object System.Collections.Generic.List[string]

foreach ($pattern in $patterns) {
  $matches = Get-ChildItem -Path $Root -Recurse -Force -File -ErrorAction SilentlyContinue |
    Where-Object {
      $relative = $_.FullName.Substring($Root.Length).TrimStart('\','/')
      $relative -like ($pattern -replace '/', '\')
    }

  foreach ($m in $matches) {
    $violations.Add($m.FullName)
  }
}

$violations = $violations | Sort-Object -Unique

if ($violations.Count -gt 0) {
  Write-Error ("Unity package build artifacts detected (must not be committed/shipped):`n" + ($violations -join "`n"))
  exit 1
}

Write-Host "OK: no Unity package build artifacts detected."