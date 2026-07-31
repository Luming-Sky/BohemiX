# Mod-pack catalog

BohemiX only indexes public Kingdom Come: Deliverance II collections on Nexus Mods and Steam Workshop. The catalog must never contain archive mirrors, file-download URLs, or collections that are private, disabled, or not intended for public player access.

Adult-content collections may be indexed when they meet the same official-platform and public-access requirements. They must set `containsAdultContent` to `true` so the download UI clearly labels them as containing adult content.

Every entry must retain its platform source as either `NexusCollection` or `SteamWorkshopCollection`, plus the official platform identifier and original page URL. The download UI uses this field to display the source and select the correct Nexus/Vortex or Steam installation flow.

When a Steam collection does not provide its own preview image, the catalog may use the first available preview from one of that collection's public child items. The image must remain on an approved official Steam image host and the entry must set `thumbnailIsRepresentative` to `true`; the UI labels it as a representative content image. Collections without either kind of image use the built-in Steam fallback cover.

The embedded catalog reviewed on 2026-07-27 contains all collections returned by the public official-platform listings at review time: 56 Nexus Collections and 342 Steam Workshop Collections. Future reviews must reconcile every listing page, remove entries that are no longer public, and add newly published collections.

## Catalog workflow

1. Review the collection on its official platform and record the review date.
2. Add the entry to `src/BohemiX.Infrastructure/Data/mod-pack-catalog.json`.
3. Validate it:

   ```powershell
   dotnet run --project tools/BohemiX.ModPackCatalogTool -- validate src/BohemiX.Infrastructure/Data/mod-pack-catalog.json
   ```

4. For remote publication, generate the signing key outside the repository, configure its public key in deployment, and sign the exact catalog bytes:

   ```powershell
   dotnet run --project tools/BohemiX.ModPackCatalogTool -- generate-key C:\secure\bohemix-modpacks-private.pem public.pem
   dotnet run --project tools/BohemiX.ModPackCatalogTool -- sign catalog.json C:\secure\bohemix-modpacks-private.pem catalog.json.sig
   ```

The tool refuses to generate or use a private key inside the repository. Never commit the private key.

## Runtime configuration

- `BOHEMIX_MODPACK_CATALOG_URL`: HTTPS URL for `catalog.json`.
- `BOHEMIX_MODPACK_CATALOG_SIGNATURE_URL`: optional detached-signature URL; defaults to the catalog URL plus `.sig`.
- `BOHEMIX_MODPACK_CATALOG_PUBLIC_KEY`: optional PEM public-key override for deployment key rotation. Escaped `\n` line breaks are accepted.

Without a remote URL, BohemiX uses the embedded reviewed catalog. A remote catalog is accepted only after ECDSA P-256/SHA-256 signature and entry validation; otherwise the last verified cache or embedded catalog is used.
