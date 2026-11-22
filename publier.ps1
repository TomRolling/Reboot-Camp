# Fabrique le .exe autonome et, si l'outil « gh » de GitHub est installé, publie la nouvelle version.
#
# Avant de lancer ce script pour une nouvelle version :
#   1. augmenter <Version> dans RebootCamp\RebootCamp.csproj (ex. 1.0.0 -> 1.0.1) ;
#   2. faire un commit.
# Les applis déjà installées proposeront la mise à jour dès que la Release existe sur GitHub.

$ErrorActionPreference = 'Stop'
$projet = Join-Path $PSScriptRoot 'RebootCamp\RebootCamp.csproj'
$version = ([xml](Get-Content $projet)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
Write-Host "Publication de la version $version..."

# Les tests automatiques vérifient les règles de sécurité du nettoyage : s'il y en a un qui échoue, on ne publie pas.
dotnet test (Join-Path $PSScriptRoot 'RebootCamp.Tests\RebootCamp.Tests.csproj') -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Un test a échoué : publication annulée." }

dotnet publish $projet -c Release -r win-x64 -p:PublishSingleFile=true --self-contained true -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "La compilation a échoué." }

$exe = Join-Path $PSScriptRoot 'RebootCamp\bin\Release\net10.0-windows\win-x64\publish\RebootCamp.exe'
Write-Host "Fichier prêt : $exe ($([Math]::Round((Get-Item $exe).Length / 1MB)) Mo)"

if (Get-Command gh -ErrorAction SilentlyContinue) {
    $reponse = Read-Host "Publier la version v$version sur GitHub maintenant ? (o/n)"
    if ($reponse -eq 'o') {
        # Le code doit être sur GitHub avant la Release : l'étiquette v$version est posée sur le dernier commit envoyé.
        git -C $PSScriptRoot push -u origin main
        if ($LASTEXITCODE -ne 0) { throw "L'envoi du code sur GitHub a échoué." }
        gh release create "v$version" $exe --repo TomRolling/Reboot-Camp --title "Version $version" --generate-notes
        if ($LASTEXITCODE -ne 0) { throw "La création de la Release a échoué." }
        Write-Host "Version v$version publiée."
    }
}
else {
    Write-Host ""
    Write-Host "Pour la publier à la main : sur la page GitHub du dépôt, « Releases » > « Draft a new release »,"
    Write-Host "étiquette v$version, glisser le fichier .exe ci-dessus, puis « Publish release »."
}
