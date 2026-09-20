# Compila e empacota o Lambda Authorizer (dotnet lambda package -> function.zip) e copia
# o pacote para fiap-esperanca-solidaria-infra/terraform/lambda-auth/function.zip, de onde
# o Terraform (terraform/k8s e terraform/docker-compose) le o arquivo.
#
# Uso:
#   ./build-and-deploy.ps1
#   ./build-and-deploy.ps1 -InfraRepoPath "C:\caminho\para\fiap-esperanca-solidaria-infra"

param(
    [string]$InfraRepoPath
)

$ErrorActionPreference = "Stop"

Push-Location -Path $PSScriptRoot

try {

    if (-not $InfraRepoPath) {
        $InfraRepoPath = Join-Path $PSScriptRoot "..\..\fiap-esperanca-solidaria-infra"
    }

    $resolvedInfraPath = Resolve-Path -Path $InfraRepoPath -ErrorAction SilentlyContinue
    if (-not $resolvedInfraPath) {
        throw "Repositorio de infra nao encontrado em '$InfraRepoPath'. Rode com -InfraRepoPath apontando para o clone local de fiap-esperanca-solidaria-infra."
    }
    $InfraRepoPath = $resolvedInfraPath.Path

    Write-Host "[build] Checking .NET SDK version..."
    $dotnetVersion = dotnet --version
    Write-Host "[build] .NET SDK version: $dotnetVersion"

    # dotnet lambda package falha se houver mais de um .sln/.csproj na mesma pasta.
    Write-Host "[build] Moving solution file out of the way..."
    Move-Item -Path "lambda-authorizer.sln" -Destination ".." -Force

    try {
        Write-Host "[build] Generating Lambda package (dotnet lambda package)..."
        dotnet lambda package -o function.zip
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet lambda package falhou com codigo $LASTEXITCODE"
        }
    }
    finally {
        Write-Host "[build] Restoring solution file..."
        Move-Item -Path "..\lambda-authorizer.sln" -Destination "." -Force
    }

    $targetDir = Join-Path $InfraRepoPath "terraform\lambda-auth"
    New-Item -ItemType Directory -Force -Path $targetDir | Out-Null

    Write-Host "[build] Copying function.zip to $targetDir ..."
    Copy-Item -Path "function.zip" -Destination (Join-Path $targetDir "function.zip") -Force

    Write-Host "[build] Done. function.zip copiado para $targetDir\function.zip"
    Write-Host "[build] Agora rode 'terraform apply' em terraform\k8s ou terraform\docker-compose (conforme o ambiente)."

}
finally {
    Pop-Location
}
