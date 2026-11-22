# Fabrique le .exe autonome et, si l'outil « gh » de GitHub est installé, publie la nouvelle version.
#
# Avant de lancer ce script pour une nouvelle version :
#   1. augmenter <Version> dans MaintenancePC\MaintenancePC.csproj (ex. 1.2.0 -> 1.3.0) ;
#   2. faire un commit.
# Les applis déjà installées proposeront la mise à jour dès que la Release existe sur GitHub.

$ErrorActionPreference = 'Stop'
$projet = Join-Path $PSScriptRoot 'MaintenancePC\MaintenancePC.csproj'
$version = ([xml](Get-Content $projet)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
Write-Host "Publication de la version $version..."

dotnet publish $projet -c Release -r win-x64 -p:PublishSingleFile=true --self-contained true -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "La compilation a échoué." }

$exe = Join-Path $PSScriptRoot 'MaintenancePC\bin\Release\net10.0-windows\win-x64\publish\RebootCamp.exe'
Write-Host "Fichier prêt : $exe ($([Math]::Round((Get-Item $exe).Length / 1MB)) Mo)"

if (Get-Command gh -ErrorAction SilentlyContinue) {
    $reponse = Read-Host "Publier la version v$version sur GitHub maintenant ? (o/n)"
    if ($reponse -eq 'o') {
        gh release create "v$version" $exe --title "Version $version" --generate-notes
        Write-Host "Version v$version publiée."
    }
}
else {
    Write-Host ""
    Write-Host "Pour la publier à la main : sur la page GitHub du dépôt, « Releases » > « Draft a new release »,"
    Write-Host "étiquette v$version, glisser le fichier .exe ci-dessus, puis « Publish release »."
}
