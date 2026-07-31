[CmdletBinding()]
param(
    [string]$SourceRoot = "D:\BohemiX\tmp\bohemix-icon-source\package\round",
    [string]$PackageRoot = "D:\BohemiX\tmp\bohemix-icon-source\package",
    [string]$OutputRoot = "D:\BohemiX\src\BohemiX.App\Assets\Icons"
)

$ErrorActionPreference = "Stop"

$icons = @(
    # Navigation
    @{ Category = "Navigation"; Name = "Home"; Source = "home" },
    @{ Category = "Navigation"; Name = "Dashboard"; Source = "dashboard" },
    @{ Category = "Navigation"; Name = "Grid"; Source = "apps" },
    @{ Category = "Navigation"; Name = "Sidebar"; Source = "view_sidebar" },
    @{ Category = "Navigation"; Name = "Menu"; Source = "menu" },
    @{ Category = "Navigation"; Name = "Search"; Source = "search" },
    @{ Category = "Navigation"; Name = "Back"; Source = "arrow_back" },
    @{ Category = "Navigation"; Name = "Forward"; Source = "arrow_forward" },
    @{ Category = "Navigation"; Name = "Up"; Source = "arrow_upward" },
    @{ Category = "Navigation"; Name = "Down"; Source = "arrow_downward" },
    @{ Category = "Navigation"; Name = "ExpandMore"; Source = "expand_more" },
    @{ Category = "Navigation"; Name = "ExpandLess"; Source = "expand_less" },
    @{ Category = "Navigation"; Name = "Fullscreen"; Source = "fullscreen" },
    @{ Category = "Navigation"; Name = "FullscreenExit"; Source = "fullscreen_exit" },
    @{ Category = "Navigation"; Name = "External"; Source = "open_in_new" },
    @{ Category = "Navigation"; Name = "More"; Source = "more_horiz" },

    # Actions
    @{ Category = "Actions"; Name = "Add"; Source = "add" },
    @{ Category = "Actions"; Name = "Remove"; Source = "remove" },
    @{ Category = "Actions"; Name = "Close"; Source = "close" },
    @{ Category = "Actions"; Name = "Check"; Source = "check" },
    @{ Category = "Actions"; Name = "Refresh"; Source = "refresh" },
    @{ Category = "Actions"; Name = "Settings"; Source = "settings" },
    @{ Category = "Actions"; Name = "Download"; Source = "download" },
    @{ Category = "Actions"; Name = "Upload"; Source = "upload" },
    @{ Category = "Actions"; Name = "Save"; Source = "save" },
    @{ Category = "Actions"; Name = "Delete"; Source = "delete" },
    @{ Category = "Actions"; Name = "Edit"; Source = "edit" },
    @{ Category = "Actions"; Name = "Copy"; Source = "content_copy" },
    @{ Category = "Actions"; Name = "Undo"; Source = "undo" },
    @{ Category = "Actions"; Name = "Redo"; Source = "redo" },
    @{ Category = "Actions"; Name = "Play"; Source = "play_arrow" },
    @{ Category = "Actions"; Name = "Pause"; Source = "pause" },
    @{ Category = "Actions"; Name = "Stop"; Source = "stop" },

    # Files and data
    @{ Category = "FilesAndData"; Name = "Folder"; Source = "folder" },
    @{ Category = "FilesAndData"; Name = "FolderOpen"; Source = "folder_open" },
    @{ Category = "FilesAndData"; Name = "FolderAdd"; Source = "create_new_folder" },
    @{ Category = "FilesAndData"; Name = "File"; Source = "insert_drive_file" },
    @{ Category = "FilesAndData"; Name = "Document"; Source = "description" },
    @{ Category = "FilesAndData"; Name = "Article"; Source = "article" },
    @{ Category = "FilesAndData"; Name = "Archive"; Source = "archive" },
    @{ Category = "FilesAndData"; Name = "Unarchive"; Source = "unarchive" },
    @{ Category = "FilesAndData"; Name = "Package"; Source = "inventory_2" },
    @{ Category = "FilesAndData"; Name = "Cloud"; Source = "cloud" },
    @{ Category = "FilesAndData"; Name = "CloudDownload"; Source = "cloud_download" },
    @{ Category = "FilesAndData"; Name = "CloudUpload"; Source = "cloud_upload" },
    @{ Category = "FilesAndData"; Name = "Storage"; Source = "storage" },
    @{ Category = "FilesAndData"; Name = "Backup"; Source = "backup" },
    @{ Category = "FilesAndData"; Name = "Restore"; Source = "restore" },
    @{ Category = "FilesAndData"; Name = "History"; Source = "history" },

    # Status and feedback
    @{ Category = "Status"; Name = "Info"; Source = "info" },
    @{ Category = "Status"; Name = "Help"; Source = "help" },
    @{ Category = "Status"; Name = "Warning"; Source = "warning" },
    @{ Category = "Status"; Name = "Error"; Source = "error" },
    @{ Category = "Status"; Name = "Success"; Source = "check_circle" },
    @{ Category = "Status"; Name = "Cancel"; Source = "cancel" },
    @{ Category = "Status"; Name = "Hourglass"; Source = "hourglass_empty" },
    @{ Category = "Status"; Name = "Clock"; Source = "schedule" },
    @{ Category = "Status"; Name = "Notification"; Source = "notifications" },
    @{ Category = "Status"; Name = "NotificationOff"; Source = "notifications_off" },
    @{ Category = "Status"; Name = "Visible"; Source = "visibility" },
    @{ Category = "Status"; Name = "Hidden"; Source = "visibility_off" },
    @{ Category = "Status"; Name = "Lock"; Source = "lock" },
    @{ Category = "Status"; Name = "Unlock"; Source = "lock_open" },
    @{ Category = "Status"; Name = "Shield"; Source = "shield" },
    @{ Category = "Status"; Name = "Verified"; Source = "verified" },

    # People and community
    @{ Category = "People"; Name = "Person"; Source = "person" },
    @{ Category = "People"; Name = "Users"; Source = "group" },
    @{ Category = "People"; Name = "PersonAdd"; Source = "person_add" },
    @{ Category = "People"; Name = "GroupAdd"; Source = "group_add" },
    @{ Category = "People"; Name = "Account"; Source = "account_circle" },
    @{ Category = "People"; Name = "Badge"; Source = "badge" },
    @{ Category = "People"; Name = "Contact"; Source = "contact_page" },
    @{ Category = "People"; Name = "Forum"; Source = "forum" },
    @{ Category = "People"; Name = "Chat"; Source = "chat" },
    @{ Category = "People"; Name = "Mail"; Source = "mail" },
    @{ Category = "People"; Name = "Share"; Source = "share" },
    @{ Category = "People"; Name = "Globe"; Source = "public" },
    @{ Category = "People"; Name = "Language"; Source = "language" },
    @{ Category = "People"; Name = "Favorite"; Source = "favorite" },
    @{ Category = "People"; Name = "Star"; Source = "star" },
    @{ Category = "People"; Name = "Premium"; Source = "workspace_premium" },

    # Game and progression
    @{ Category = "Game"; Name = "Gamepad"; Source = "sports_esports" },
    @{ Category = "Game"; Name = "Puzzle"; Source = "extension" },
    @{ Category = "Game"; Name = "Compass"; Source = "explore" },
    @{ Category = "Game"; Name = "Map"; Source = "map" },
    @{ Category = "Game"; Name = "Quest"; Source = "flag" },
    @{ Category = "Game"; Name = "Trophy"; Source = "emoji_events" },
    @{ Category = "Game"; Name = "Achievement"; Source = "military_tech" },
    @{ Category = "Game"; Name = "Spark"; Source = "auto_awesome" },
    @{ Category = "Game"; Name = "Zap"; Source = "bolt" },
    @{ Category = "Game"; Name = "Flame"; Source = "local_fire_department" },
    @{ Category = "Game"; Name = "Dice"; Source = "casino" },
    @{ Category = "Game"; Name = "Token"; Source = "token" },
    @{ Category = "Game"; Name = "Diamond"; Source = "diamond" },
    @{ Category = "Game"; Name = "Coin"; Source = "paid" },
    @{ Category = "Game"; Name = "Gift"; Source = "card_giftcard" },
    @{ Category = "Game"; Name = "Leaderboard"; Source = "leaderboard" },

    # Alchemy
    @{ Category = "Alchemy"; Name = "Flask"; Source = "science" },
    @{ Category = "Alchemy"; Name = "Biotech"; Source = "biotech" },
    @{ Category = "Alchemy"; Name = "Potion"; Source = "medication_liquid" },
    @{ Category = "Alchemy"; Name = "Water"; Source = "water_drop" },
    @{ Category = "Alchemy"; Name = "Oil"; Source = "oil_barrel" },
    @{ Category = "Alchemy"; Name = "Drink"; Source = "local_drink" },
    @{ Category = "Alchemy"; Name = "Wine"; Source = "wine_bar" },
    @{ Category = "Alchemy"; Name = "Spirits"; Source = "liquor" },
    @{ Category = "Alchemy"; Name = "Herb"; Source = "spa" },
    @{ Category = "Alchemy"; Name = "Leaf"; Source = "energy_savings_leaf" },
    @{ Category = "Alchemy"; Name = "Grass"; Source = "grass" },
    @{ Category = "Alchemy"; Name = "Filter"; Source = "filter_alt" },
    @{ Category = "Alchemy"; Name = "Scale"; Source = "scale" },
    @{ Category = "Alchemy"; Name = "Timer"; Source = "timer" },
    @{ Category = "Alchemy"; Name = "Temperature"; Source = "device_thermostat" },
    @{ Category = "Alchemy"; Name = "Recipe"; Source = "menu_book" },

    # Forge and workshop
    @{ Category = "Forge"; Name = "Hammer"; Source = "hardware" },
    @{ Category = "Forge"; Name = "Construction"; Source = "construction" },
    @{ Category = "Forge"; Name = "Handyman"; Source = "handyman" },
    @{ Category = "Forge"; Name = "Build"; Source = "build" },
    @{ Category = "Forge"; Name = "Engineering"; Source = "engineering" },
    @{ Category = "Forge"; Name = "Iron"; Source = "iron" },
    @{ Category = "Forge"; Name = "Hearth"; Source = "fireplace" },
    @{ Category = "Forge"; Name = "Heat"; Source = "whatshot" },
    @{ Category = "Forge"; Name = "Measure"; Source = "straighten" },
    @{ Category = "Forge"; Name = "Machine"; Source = "precision_manufacturing" },
    @{ Category = "Forge"; Name = "Tune"; Source = "settings_suggest" },
    @{ Category = "Forge"; Name = "Factory"; Source = "factory" },
    @{ Category = "Forge"; Name = "Inventory"; Source = "inventory" },
    @{ Category = "Forge"; Name = "Materials"; Source = "category" },
    @{ Category = "Forge"; Name = "Blueprint"; Source = "architecture" },
    @{ Category = "Forge"; Name = "Design"; Source = "design_services" },

    # Compatibility names for the icons currently embedded in MainWindow.axaml.
    @{ Category = "Compatibility"; Name = "ChevronDown"; Source = "expand_more" },
    @{ Category = "Compatibility"; Name = "ChevronUp"; Source = "expand_less" },
    @{ Category = "Compatibility"; Name = "ChevronRight"; Source = "chevron_right" },
    @{ Category = "Compatibility"; Name = "ArrowLeft"; Source = "arrow_back" },
    @{ Category = "Compatibility"; Name = "ArrowRight"; Source = "arrow_forward" },
    @{ Category = "Compatibility"; Name = "ArrowUp"; Source = "arrow_upward" },
    @{ Category = "Compatibility"; Name = "ArrowDown"; Source = "arrow_downward" },
    @{ Category = "Compatibility"; Name = "Plus"; Source = "add" },
    @{ Category = "Compatibility"; Name = "Trash"; Source = "delete" },
    @{ Category = "Compatibility"; Name = "Newspaper"; Source = "newspaper" },
    @{ Category = "Compatibility"; Name = "Trend"; Source = "trending_up" },
    @{ Category = "Compatibility"; Name = "Palette"; Source = "color_lens" },
    @{ Category = "Compatibility"; Name = "Database"; Source = "storage" },
    @{ Category = "Compatibility"; Name = "Sword"; Source = "custom"; PathData = "M14,4 L20,4 L20,10 L10,20 L6,16 Z M5,19 L9,15 M8,12 L12,16 M3,21 L6,18" },
    @{ Category = "Compatibility"; Name = "Bag"; Source = "shopping_bag" },
    @{ Category = "Compatibility"; Name = "BedSave"; Source = "custom"; PathData = "M4,8 L9,5 L9,16 L4,19 Z M9,7 L14,4 L22,8 L17,11 Z M9,9 L17,13 L22,10 L22,15 L17,20 L9,16 Z M17,13 L22,10 L22,15 L17,20 Z M11,8 L14,6.4 L18,8.4 L15,10.1 Z M12,11.2 L17,13.8 M15,9.7 L20,12.1 M6,18 L8.5,16.6 L8.5,21 L6,22 Z M18.5,19 L21,16 L21,21 L18.5,22 Z" }
)

if ($icons.Count -ne 145) {
    throw "Expected 145 icons, found $($icons.Count)."
}

New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null

$resolvedIcons = foreach ($icon in $icons) {
    if ($icon.PathData) {
        $pathData = $icon.PathData
    } else {
        $sourcePath = Join-Path $SourceRoot ($icon.Source + ".svg")
        if (-not (Test-Path -LiteralPath $sourcePath)) {
            throw "Missing source icon: $sourcePath"
        }

        [xml]$svg = Get-Content -Raw -Encoding UTF8 $sourcePath
        $pathData = @($svg.svg.path | ForEach-Object { $_.d }) -join " "
    }
    if ([string]::IsNullOrWhiteSpace($pathData)) {
        throw "No SVG path data found: $sourcePath"
    }

    [pscustomobject]@{
        Category = $icon.Category
        Name = $icon.Name
        Key = "BmxIcon$($icon.Name)"
        Source = $icon.Source
        Path = $pathData
    }
}

$xamlLines = @(
    '<Styles xmlns="https://github.com/avaloniaui"'
    '        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">'
    '    <Styles.Resources>'
    '        <!-- BohemiX 24x24 rounded filled icon library. See LICENSE-MATERIAL-ICONS.txt. -->'
)
foreach ($icon in $resolvedIcons) {
    $xamlLines += "        <StreamGeometry x:Key=`"$($icon.Key)`">$($icon.Path)</StreamGeometry>"
}
$xamlLines += @('    </Styles.Resources>', '</Styles>')
Set-Content -LiteralPath (Join-Path $OutputRoot 'BohemiXIcons.axaml') -Value $xamlLines -Encoding UTF8

$manifest = [ordered]@{
    schemaVersion = 1
    name = "BohemiX Icon Library"
    count = $resolvedIcons.Count
    keyPrefix = "BmxIcon"
    viewBox = "0 0 24 24"
    style = "rounded filled geometric glyphs"
    palette = @("#F8FAFC", "#E5E7EB", "#CBD5E1", "#A6ADBB", "#94A3B8", "#64748B", "#F59E0B", "#75C394", "#FCA5A5")
    source = "Material Design Icons SVG 0.14.15, round variant"
    license = "Apache-2.0"
    icons = @($resolvedIcons | Select-Object Category, Name, Key, Source)
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputRoot 'manifest.json') -Encoding UTF8

$styleSpec = [ordered]@{
    name = "bohemix-rounded-glyph"
    preamble = "A cohesive library of compact, rounded, solid geometric glyphs for the BohemiX desktop application. Medieval game-management metaphors are reduced to one bold silhouette with calm modern geometry. Cool white and neutral grey forms stay legible over both pale-blue and deep-blue application surfaces. Near-black is never the dominant fill."
    palette = @("#F8FAFC", "#E5E7EB", "#CBD5E1", "#A6ADBB", "#94A3B8", "#64748B", "#F59E0B", "#75C394", "#FCA5A5")
    cutout = "floodfill"
    threshold = 30
    construction = [ordered]@{
        grid = "24x24 keyline; 20x20 maximum live area; optical centering"
        angles = "horizontal, vertical, and 45-degree diagonals"
        stroke = $null
        corner_radius = "rounded terminals and 1-2 unit internal radii"
        fill_rule = "single solid silhouette with intentional negative-space cutouts"
        color_rule = "one foreground color per glyph; semantic accent is applied by the consuming UI"
        detail_budget = "one metaphor, usually 1-3 joined masses, no micro-detail"
        shared_parts = "consistent circles, chevrons, plus/minus signs, document corners, and person heads"
        perspective = "flat front view"
        shadow = "none"
        motif = "soft corner cuts and compact central negative space"
        weight_hierarchy = "the silhouette remains visually dominant; no internal accent outweighs it"
    }
}
$styleSpec | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputRoot 'style-spec.json') -Encoding UTF8

$html = [System.Text.StringBuilder]::new()
[void]$html.AppendLine('<!doctype html>')
[void]$html.AppendLine('<html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">')
[void]$html.AppendLine('<title>BohemiX Icon Library</title>')
[void]$html.AppendLine('<style>body{margin:0;background:#0b0d12;color:#f4f8ff;font-family:Inter,Segoe UI,sans-serif}main{max-width:1440px;margin:auto;padding:40px}h1{font-size:28px;margin:0 0 8px}p{color:#8e9aae;margin:0 0 36px}h2{font-size:16px;margin:32px 0 12px;color:#93c5fd}.grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(132px,1fr));gap:8px}.icon{background:#151a22;border:1px solid #2e3540;border-radius:6px;min-height:112px;display:grid;place-items:center;padding:12px}.icon svg{width:34px;height:34px;fill:#f4f8ff}.icon:hover{border-color:#60a5fa;background:#19243a}.name{font-size:12px;color:#b8c0cc;margin-top:10px;text-align:center;overflow-wrap:anywhere}.key{font-size:10px;color:#667085;margin-top:3px;text-align:center;overflow-wrap:anywhere}</style></head><body><main>')
[void]$html.AppendLine("<h1>BohemiX Icon Library</h1><p>$($resolvedIcons.Count) rounded 24x24 glyphs · Avalonia StreamGeometry · BmxIcon prefix</p>")
foreach ($category in $resolvedIcons.Category | Select-Object -Unique) {
    [void]$html.AppendLine("<h2>$category</h2><section class=`"grid`">")
    foreach ($icon in $resolvedIcons | Where-Object Category -eq $category) {
        [void]$html.AppendLine("<article class=`"icon`"><div><svg viewBox=`"0 0 24 24`" aria-hidden=`"true`"><path d=`"$($icon.Path)`"/></svg><div class=`"name`">$($icon.Name)</div><div class=`"key`">$($icon.Key)</div></div></article>")
    }
    [void]$html.AppendLine('</section>')
}
[void]$html.AppendLine('</main></body></html>')
Set-Content -LiteralPath (Join-Path $OutputRoot 'catalog.html') -Value $html.ToString() -Encoding UTF8

$readme = @'
# BohemiX Icon Library

This directory contains 144 globally registered, 24x24 rounded glyphs for Avalonia.

Use any icon through its `BmxIcon` resource key:

```xml
<PathIcon Data="{StaticResource BmxIconDownload}"
          Width="16"
          Height="16"
          Foreground="#F4F8FF" />
```

Files:

- `BohemiXIcons.axaml`: application-ready `StreamGeometry` resources.
- `catalog.html`: visual browser for every icon.
- `manifest.json`: searchable category/name/source mapping.
- `style-spec.json`: frozen oil-icon style specification for future batches.
- `LICENSE-MATERIAL-ICONS.txt`: upstream Apache 2.0 license.
- `Spot/`: 32 transparent 256x256 PNG spot icons, prompts, and their slicing manifest.
- `../../../../tools/Recolor-BohemiXSpotIcons.py`: deterministic blue-to-neutral palette migration tool.

The UI chooses icon color semantically. Default glyphs use the foreground color; blue is interactive, amber is emphasis, green is success, and red is destructive/error. Keep functional glyphs at 12-24 px. Use generated transparent PNG spot icons only at larger sizes.
'@
Set-Content -LiteralPath (Join-Path $OutputRoot 'README.md') -Value $readme -Encoding UTF8

Copy-Item -LiteralPath (Join-Path $PackageRoot 'LICENSE') -Destination (Join-Path $OutputRoot 'LICENSE-MATERIAL-ICONS.txt') -Force

Write-Output "Generated $($resolvedIcons.Count) icons in $OutputRoot"
