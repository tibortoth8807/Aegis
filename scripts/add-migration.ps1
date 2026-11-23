# Creates (or adds) an EF Core code-first migration from the current model.
#   ./scripts/add-migration.ps1 InitialCreate
# The API applies pending migrations at start; without any migration it creates the schema from the model.
param([Parameter(Mandatory)] [string] $Name)
dotnet tool restore 2>$null; if (-not (Get-Command dotnet-ef -ErrorAction SilentlyContinue)) { dotnet tool install --global dotnet-ef }
$env:Database__Provider = 'SqlServer'
dotnet ef migrations add $Name --project src/Aegis.Infrastructure --startup-project src/Aegis.Api --output-dir Persistence/Migrations
