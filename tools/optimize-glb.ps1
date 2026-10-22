<#
  Сжатие .glb для Deep Garage без Node и Draco.
  Пример:
    powershell -ExecutionPolicy Bypass -File tools\optimize-glb.ps1 `
      -In "C:\Downloads\car.glb" -Out models\car.glb -Tris 140000 -MaxTex 1024 `
      -DropMaterials "coat,Material" -DropNodes "engine|shadow"
#>
param(
  [Parameter(Mandatory=$true)][string]$In,
  [Parameter(Mandatory=$true)][string]$Out,
  [int]$Tris = 140000,
  [int]$MaxTex = 1024,
  [string]$DropMaterials = "",
  [string]$DropNodes = ""
)
$src = Join-Path $PSScriptRoot "GlbOptimizer.cs"
if (-not ("GlbOptimizer" -as [type])) {
  Add-Type -Path $src -ReferencedAssemblies System.Drawing, System.Web.Extensions, System.Core
}
$outFull = if ([IO.Path]::IsPathRooted($Out)) { $Out } else { [IO.Path]::GetFullPath((Join-Path (Get-Location) $Out)) }
[GlbOptimizer]::Run([IO.Path]::GetFullPath($In), $outFull, $Tris, $MaxTex, $DropMaterials, $DropNodes) | Out-Null
