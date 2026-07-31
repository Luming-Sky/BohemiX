# Nexus Mods Account Binding

BohemiX uses a dedicated Edge WebView2 session for the default Nexus Mods binding flow. Credentials are entered only on the Nexus Mods website inside that window; BohemiX never receives the account password.

## Binding

1. Open Player Profiles, or select Nexus Mods as the mod download source.
2. Select **Bind account** or **Browser authorization**.
3. Sign in on the Nexus Mods page displayed by BohemiX.
4. The authorization window closes automatically after a valid Nexus login session is detected.

The Nexus cookies remain in a dedicated WebView2 user-data directory on this computer. BohemiX attaches them only to validated `nexusmods.com` hosts and does not log their values. A personal API Key can still be pasted in the existing binding dialog as a fallback; it is validated through `https://api.nexusmods.com/v1/users/validate.json` and encrypted with Windows Data Protection.

## Unbinding

Select **Unbind** in Player Profiles. BohemiX clears the Nexus cookies from its dedicated WebView2 profile and deletes any encrypted API Key binding.

## Requirements

The browser binding flow requires Microsoft Edge WebView2 Runtime, which is included with current Windows installations. It does not require a Nexus SSO application slug.

## Download Availability

BohemiX reuses the bound browser session for Nexus pages and download-link generation. Direct download availability remains subject to the bound account's membership and Nexus Mods policies. The personal API Key fallback is sent only to the official Nexus Mods API.
