# Search objective contracts

Run `dotnet run --project tools/SearchObjectiveChecks/SearchObjectiveChecks.csproj -c Release`.

Links production objective and ordering code; only the runtime result DTO is stubbed. Covers the 30 HP default, reward constraints, custom reserves, and balanced loss-before-growth ordering. Does not run game simulation, beam search, potion eligibility, or UI.
