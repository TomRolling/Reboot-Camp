# Fabrique le .exe autonome sur ce PC, pour l'essayer avant de l'envoyer.
#
# La publication, elle, est automatique : chaque « git push » sur la branche main lance les tests sur GitHub,
# fabrique le .exe et crée une Release (voir .github\workflows\publication.yml). Les applis déjà installées
# proposent alors la mise à jour à leur prochain lancement.

$ErrorActionPreference = 'Stop'
$projet = Join-Path $PSScriptRoot 'RebootCamp\RebootCamp.csproj'

dotnet test (Join-Path $PSScriptRoot 'RebootCamp.Tests\RebootCamp.Tests.csproj') -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Un test a échoué." }

# Dans un dossier à part : le .exe se fabrique même si l'appli est ouverte.
$sortie = Join-Path $PSScriptRoot 'sortie'
dotnet publish $projet -c Release -r win-x64 -p:PublishSingleFile=true --self-contained true -nologo -v q -o $sortie
if ($LASTEXITCODE -ne 0) { throw "La compilation a échoué." }

$exe = Join-Path $sortie 'RebootCamp.exe'
Write-Host "Fichier prêt : $exe ($([Math]::Round((Get-Item $exe).Length / 1MB)) Mo)"
