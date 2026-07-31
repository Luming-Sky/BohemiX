# Nexus Mods Browser Login Troubleshooting

Browser login is the default Nexus Mods binding flow. See `docs/NexusAccountBinding.md` for the complete account binding behavior.

BohemiX stores the Nexus login only in its dedicated Edge WebView2 profile. It does not read the user's normal Chrome, Edge, Firefox, or Brave profiles.

## Login Window Does Not Open

Install or repair Microsoft Edge WebView2 Runtime, then retry **Bind account** from Player Profiles.

## Cookie Expired

If Nexus returns 401 or 403, unbind the account and run the browser login again. The authorization window closes automatically when the Nexus session is detected.

## Login Is Not Detected

Finish the Nexus login inside the BohemiX window rather than a separate browser. If automatic detection does not close the window, select **I have signed in** once the Nexus account page is visible.

## Rate Limit

Nexus Mods may return HTTP 429 when requests are too frequent. BohemiX respects `Retry-After` when present and otherwise uses exponential backoff before retrying.

## Revoke Access

Select **Unbind** in Player Profiles. BohemiX removes the Nexus cookies from its dedicated WebView2 session. A personal API Key binding can also be removed from the same account row.
