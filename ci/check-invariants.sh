#!/usr/bin/env bash
set -euo pipefail

# Lanes 3 + 5b: BannedApiAnalyzers violations are already errors in each csproj (RS0030).
# -warnaserror also promotes any remaining warnings to errors as belt-and-suspenders.
dotnet build Kozmo.sln -c Release -warnaserror

# Lanes 1, 2, 4, 5a: architecture + invariant reflection tests.
dotnet test tests/Kozmo.Architecture.Tests -c Release --filter "Category=Invariant"
