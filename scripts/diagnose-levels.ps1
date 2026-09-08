<#
.SYNOPSIS
    Read-only diagnosis for Season/Episode rating support (plan phase 0).

.DESCRIPTION
    Answers the questions that decide how episode matching must be built, without
    changing anything:

      A) Library inventory - item counts per level, provider-id coverage, the fields
                             episode matching depends on, and how many items already
                             carry a CommunityRating.
      B) Dataset hit rate  - route A (episode's own IMDb id) vs route B (series tconst
                             + season/episode number via title.episode.tsv), a
                             cross-check of the two, and a control set that needs no
                             library at all.

    Everything here is GET-only against Jellyfin plus a plain download from IMDb.
    Nothing is written to the server and mdblist is never contacted.

    The IMDb datasets are licensed for personal and non-commercial use only. They are
    cached under -WorkDir and must never be committed or redistributed.

.PARAMETER ServerUrl
    Jellyfin base url. Defaults to the local test instance.

.PARAMETER ApiKeyFile
    File holding the API key. Defaults to the test instance's key file.

.PARAMETER WorkDir
    Where the IMDb datasets are cached and the report is written.

.PARAMETER RefreshDatasets
    Re-download the IMDb datasets even if the cached copies are still fresh.

.EXAMPLE
    pwsh -File scripts/diagnose-levels.ps1
#>
[CmdletBinding()]
param(
    [string] $ServerUrl = 'http://localhost:8096',
    [string] $ApiKeyFile = 'D:\Claude\jellyfin\apikey.txt',
    [string] $WorkDir = (Join-Path ([IO.Path]::GetTempPath()) 'externalratings-diagnosis'),
    [switch] $RefreshDatasets
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$InformationPreference = 'Continue'

$RatingsUrl = 'https://datasets.imdbws.com/title.ratings.tsv.gz'
$EpisodeUrl = 'https://datasets.imdbws.com/title.episode.tsv.gz'
$DatasetMaxAgeHours = 24

# Control set for B2: deliberately mixed - long-running western shows, anthologies,
# anime with seasonal vs absolute numbering, and shows with many specials.
$ControlSeries = [ordered]@{
    'tt0903747' = 'Breaking Bad'
    'tt0944947' = 'Game of Thrones'
    'tt0108778' = 'Friends'
    'tt0386676' = 'The Office (US)'
    'tt0096697' = 'The Simpsons'
    'tt0121955' = 'South Park'
    'tt0141842' = 'The Sopranos'
    'tt0306414' = 'The Wire'
    'tt2861424' = 'Rick and Morty'
    'tt1475582' = 'Sherlock'
    'tt0417299' = 'Avatar: The Last Airbender'
    'tt4574334' = 'Stranger Things'
    'tt1520211' = 'The Walking Dead'
    'tt2356777' = 'True Detective'
    'tt0475784' = 'Westworld'
    'tt7366338' = 'Chernobyl'
    'tt0795176' = 'Planet Earth'
    'tt1806234' = 'Black Mirror'
    'tt0081912' = 'Cosmos'
    'tt0213338' = 'Cowboy Bebop'
    'tt0388629' = 'One Piece'
    'tt0409591' = 'Naruto'
    'tt2560140' = 'Attack on Titan'
    'tt9335498' = 'Demon Slayer'
    'tt0877057' = 'Death Note'
    'tt1355642' = 'Fullmetal Alchemist Brotherhood'
    'tt2098220' = 'Hunter x Hunter (2011)'
    'tt5626028' = 'My Hero Academia'
    'tt0182629' = 'Neon Genesis Evangelion'
    'tt1910272' = 'Steins;Gate'
    'tt0436992' = 'Doctor Who (2005)'
    'tt0056751' = 'Doctor Who (1963)'
    'tt0098904' = 'Seinfeld'
    'tt1439629' = 'Community'
    'tt2467372' = 'Brooklyn Nine-Nine'
    'tt2802850' = 'Fargo'
    'tt0052520' = 'The Twilight Zone'
    'tt3032476' = 'Better Call Saul'
    'tt5555260' = 'This Is Us'
}

#region helpers -----------------------------------------------------------------

function Write-Section {
    param([string] $Title)
    Write-Information ''
    Write-Information ("=== $Title ".PadRight(78, '='))
}

function Get-JellyfinHeaders {
    param([string] $KeyFile)
    if (-not (Test-Path -LiteralPath $KeyFile)) {
        throw "API key file not found: $KeyFile"
    }
    $key = (Get-Content -LiteralPath $KeyFile -Raw).Trim()
    if ([string]::IsNullOrWhiteSpace($key)) {
        throw "API key file is empty: $KeyFile"
    }
    return @{ Authorization = ('MediaBrowser Token="{0}"' -f $key) }
}

function Invoke-Jellyfin {
    # GET only. Never call this with anything that mutates.
    param(
        [string] $BaseUrl,
        [hashtable] $Headers,
        [string] $Path,
        [hashtable] $Query = @{}
    )
    $pairs = foreach ($k in $Query.Keys) {
        '{0}={1}' -f $k, [Uri]::EscapeDataString([string]$Query[$k])
    }
    $uri = '{0}{1}' -f $BaseUrl.TrimEnd('/'), $Path
    if ($pairs) { $uri = $uri + '?' + ($pairs -join '&') }
    return Invoke-RestMethod -Method Get -Uri $uri -Headers $Headers -TimeoutSec 120
}

function Get-ImdbNumericId {
    # tt0903747 -> 903747. Returns $null for anything that is not a tconst.
    param([string] $ImdbId)
    if ([string]::IsNullOrWhiteSpace($ImdbId)) { return $null }
    $t = $ImdbId.Trim()
    if (-not $t.StartsWith('tt', [StringComparison]::OrdinalIgnoreCase)) { return $null }
    [long] $n = 0
    if ([long]::TryParse($t.Substring(2), [ref] $n)) { return $n }
    return $null
}

function Format-Pct {
    param([int] $Part, [int] $Total)
    if ($Total -le 0) { return '   n/a' }
    return '{0,6:P1}' -f ($Part / $Total)
}

function Get-Dataset {
    param([string] $Url, [string] $Path, [switch] $Force)
    $fresh = $false
    if (Test-Path -LiteralPath $Path) {
        $age = (Get-Date).ToUniversalTime() - (Get-Item -LiteralPath $Path).LastWriteTimeUtc
        $fresh = $age.TotalHours -lt $DatasetMaxAgeHours
    }
    if ($fresh -and -not $Force) {
        Write-Information ('  cached   {0} ({1:N1} MB)' -f (Split-Path -Leaf $Path), ((Get-Item -LiteralPath $Path).Length / 1MB))
        return
    }
    Write-Information ('  fetching {0}' -f $Url)
    $old = $ProgressPreference
    $ProgressPreference = 'SilentlyContinue'
    try { Invoke-WebRequest -Uri $Url -OutFile $Path -TimeoutSec 900 }
    finally { $ProgressPreference = $old }
    Write-Information ('  saved    {0} ({1:N1} MB)' -f (Split-Path -Leaf $Path), ((Get-Item -LiteralPath $Path).Length / 1MB))
}

function Open-GzipReader {
    param([string] $Path)
    $fs = [IO.File]::OpenRead($Path)
    $gz = [IO.Compression.GZipStream]::new($fs, [IO.Compression.CompressionMode]::Decompress)
    return [IO.StreamReader]::new($gz)
}

#endregion

#region dataset loading ---------------------------------------------------------

function Import-RatingsIndex {
    # Loads title.ratings into dictionaries keyed by numeric tconst (~1.6M rows).
    param([string] $Path)

    $sw = [Diagnostics.Stopwatch]::StartNew()
    $rating = [Collections.Generic.Dictionary[long, single]]::new(2000000)
    $votes = [Collections.Generic.Dictionary[long, int]]::new(2000000)
    $inv = [Globalization.CultureInfo]::InvariantCulture
    $reader = Open-GzipReader -Path $Path
    try {
        $null = $reader.ReadLine()
        while ($null -ne ($line = $reader.ReadLine())) {
            $t1 = $line.IndexOf("`t")
            if ($t1 -le 0) { continue }
            $t2 = $line.IndexOf("`t", $t1 + 1)
            if ($t2 -le $t1) { continue }

            $id = Get-ImdbNumericId $line.Substring(0, $t1)
            if ($null -eq $id) { continue }

            [single] $avg = 0
            if (-not [single]::TryParse($line.Substring($t1 + 1, $t2 - $t1 - 1), [Globalization.NumberStyles]::Float, $inv, [ref] $avg)) { continue }

            [int] $nv = 0
            [void][int]::TryParse($line.Substring($t2 + 1), [Globalization.NumberStyles]::Integer, $inv, [ref] $nv)

            $rating[$id] = $avg
            $votes[$id] = $nv
        }
    }
    finally { $reader.Dispose() }

    $sw.Stop()
    Write-Information ('  title.ratings: {0:N0} rated titles in {1:N1}s' -f $rating.Count, $sw.Elapsed.TotalSeconds)
    return [pscustomobject]@{ Rating = $rating; Votes = $votes }
}

function Import-EpisodeIndex {
    <#
    Streams title.episode (~9M rows) and keeps only rows whose parentTconst is in
    $ParentFilter. That is the whole point: never hold 9M rows in memory.

    Returns
      BySE      : "parent|season|episode" -> child numeric tconst
      ByChild   : child numeric tconst    -> parent/season/episode
      ByParent  : parent numeric tconst   -> list of child numeric tconsts
    #>
    param(
        [string] $Path,
        [Collections.Generic.HashSet[long]] $ParentFilter
    )

    $sw = [Diagnostics.Stopwatch]::StartNew()
    $bySE = [Collections.Generic.Dictionary[string, long]]::new()
    $byChild = [Collections.Generic.Dictionary[long, object]]::new()
    $byParent = [Collections.Generic.Dictionary[long, object]]::new()
    $scanned = 0
    $ambiguous = 0

    $reader = Open-GzipReader -Path $Path
    try {
        $null = $reader.ReadLine()
        while ($null -ne ($line = $reader.ReadLine())) {
            $scanned++
            $parts = $line.Split("`t")
            if ($parts.Length -lt 4) { continue }

            $parentId = Get-ImdbNumericId $parts[1]
            if ($null -eq $parentId) { continue }
            if (-not $ParentFilter.Contains($parentId)) { continue }

            $childId = Get-ImdbNumericId $parts[0]
            if ($null -eq $childId) { continue }

            [int] $season = -1
            [int] $episode = -1
            $hasSeason = [int]::TryParse($parts[2], [ref] $season)
            $hasEpisode = [int]::TryParse($parts[3], [ref] $episode)

            $byChild[$childId] = [pscustomobject]@{
                Parent  = $parentId
                Season  = $(if ($hasSeason) { $season } else { $null })
                Episode = $(if ($hasEpisode) { $episode } else { $null })
            }

            if (-not $byParent.ContainsKey($parentId)) {
                $byParent[$parentId] = [Collections.Generic.List[long]]::new()
            }
            $byParent[$parentId].Add($childId)

            if ($hasSeason -and $hasEpisode) {
                $key = '{0}|{1}|{2}' -f $parentId, $season, $episode
                if ($bySE.ContainsKey($key)) { $ambiguous++ } else { $bySE[$key] = $childId }
            }
        }
    }
    finally { $reader.Dispose() }

    $sw.Stop()
    Write-Information ('  title.episode: scanned {0:N0} rows, kept {1:N0} for {2:N0} series in {3:N1}s ({4:N0} duplicate S/E keys)' -f `
            $scanned, $byChild.Count, $byParent.Count, $sw.Elapsed.TotalSeconds, $ambiguous)

    return [pscustomobject]@{
        BySE      = $bySE
        ByChild   = $byChild
        ByParent  = $byParent
        Ambiguous = $ambiguous
    }
}

#endregion

#region library helpers ---------------------------------------------------------

function Get-LibraryItems {
    param([string] $BaseUrl, [hashtable] $Headers)

    $fields = 'ProviderIds,ParentId,IndexNumber,ParentIndexNumber,IndexNumberEnd,CommunityRating,SeriesId,SeasonId'
    $kinds = 'Movie,Series,Season,Episode'

    # Two passes: the plugin's current query does NOT constrain IsVirtualItem, so both
    # numbers are needed to quantify what enabling the Episode level would drag in.
    $all = Invoke-Jellyfin -BaseUrl $BaseUrl -Headers $Headers -Path '/Items' -Query @{
        IncludeItemTypes = $kinds; Recursive = 'true'; Fields = $fields; EnableTotalRecordCount = 'true'
    }
    $real = Invoke-Jellyfin -BaseUrl $BaseUrl -Headers $Headers -Path '/Items' -Query @{
        IncludeItemTypes = $kinds; Recursive = 'true'; Fields = $fields; IsVirtualItem = 'false'; EnableTotalRecordCount = 'true'
    }
    return [pscustomobject]@{ All = @($all.Items); Real = @($real.Items) }
}

function Get-Prop {
    param($Item, [string] $Name)
    if ($null -eq $Item) { return $null }
    if ($Item.PSObject.Properties.Name -notcontains $Name) { return $null }
    return $Item.$Name
}

function Get-ProviderId {
    param($Item, [string] $Name)
    $ids = Get-Prop -Item $Item -Name 'ProviderIds'
    if ($null -eq $ids) { return $null }
    $p = $ids.PSObject.Properties | Where-Object { $_.Name -ieq $Name } | Select-Object -First 1
    if ($null -eq $p) { return $null }
    if ([string]::IsNullOrWhiteSpace([string]$p.Value)) { return $null }
    return [string]$p.Value
}

#endregion

#region main --------------------------------------------------------------------

New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
$datasetDir = Join-Path $WorkDir 'imdb'
New-Item -ItemType Directory -Force -Path $datasetDir | Out-Null
$ratingsPath = Join-Path $datasetDir 'title.ratings.tsv.gz'
$episodePath = Join-Path $datasetDir 'title.episode.tsv.gz'

Write-Section 'Datasets'
Get-Dataset -Url $RatingsUrl -Path $ratingsPath -Force:$RefreshDatasets
Get-Dataset -Url $EpisodeUrl -Path $episodePath -Force:$RefreshDatasets
$ratings = Import-RatingsIndex -Path $ratingsPath

Write-Section 'A) Library inventory'
$headers = Get-JellyfinHeaders -KeyFile $ApiKeyFile
$items = Get-LibraryItems -BaseUrl $ServerUrl -Headers $headers

$levels = @('Movie', 'Series', 'Season', 'Episode')
$byType = @{}
$virtualCount = @{}
foreach ($t in $levels) {
    $byType[$t] = @($items.Real | Where-Object { $_.Type -eq $t })
    $virtualCount[$t] = (@($items.All | Where-Object { $_.Type -eq $t }).Count) - $byType[$t].Count
}

$series = @{}
foreach ($s in $byType['Series']) { $series[[string]$s.Id] = $s }

$episodes = $byType['Episode']
$seasons = $byType['Season']

$epStats = [ordered]@{
    Total                  = $episodes.Count
    WithOwnImdb            = 0
    ImdbEqualsSeriesImdb   = 0
    WithSeasonAndEpisodeNo = 0
    SeriesHasImdb          = 0
    MultiEpisodeFile       = 0
    Special                = 0
    HasCommunityRating     = 0
    Locked                 = 0
}

$epRows = [Collections.Generic.List[object]]::new()
foreach ($e in $episodes) {
    $ownImdb = Get-ProviderId -Item $e -Name 'Imdb'
    $seriesId = [string](Get-Prop -Item $e -Name 'SeriesId')
    $seriesItem = $null
    if ($seriesId -and $series.ContainsKey($seriesId)) { $seriesItem = $series[$seriesId] }
    $seriesImdb = Get-ProviderId -Item $seriesItem -Name 'Imdb'

    $sNo = Get-Prop -Item $e -Name 'ParentIndexNumber'
    $eNo = Get-Prop -Item $e -Name 'IndexNumber'
    $eEnd = Get-Prop -Item $e -Name 'IndexNumberEnd'
    $cr = Get-Prop -Item $e -Name 'CommunityRating'
    $locked = Get-Prop -Item $e -Name 'LockData'

    if ($ownImdb) { $epStats.WithOwnImdb++ }
    if ($ownImdb -and $seriesImdb -and $ownImdb -ieq $seriesImdb) { $epStats.ImdbEqualsSeriesImdb++ }
    if ($null -ne $sNo -and $null -ne $eNo) { $epStats.WithSeasonAndEpisodeNo++ }
    if ($seriesImdb) { $epStats.SeriesHasImdb++ }
    if ($null -ne $eEnd) { $epStats.MultiEpisodeFile++ }
    if ($sNo -eq 0) { $epStats.Special++ }
    if ($null -ne $cr) { $epStats.HasCommunityRating++ }
    if ($locked) { $epStats.Locked++ }

    $epRows.Add([pscustomobject]@{
            Id         = [string]$e.Id
            Name       = [string]$e.Name
            SeriesName = if ($seriesItem) { [string]$seriesItem.Name } else { '' }
            OwnImdb    = $ownImdb
            SeriesImdb = $seriesImdb
            Season     = $sNo
            Episode    = $eNo
            EpisodeEnd = $eEnd
            Community  = $cr
        })
}

Write-Information ''
Write-Information ('  {0,-10} {1,8} {2,10} {3,12}' -f 'Level', 'real', 'virtual', 'has rating')
foreach ($t in $levels) {
    $withRating = @($byType[$t] | Where-Object { $null -ne (Get-Prop -Item $_ -Name 'CommunityRating') }).Count
    Write-Information ('  {0,-10} {1,8} {2,10} {3,12}' -f $t, $byType[$t].Count, $virtualCount[$t], $withRating)
}

$seasonsWithAnyId = @($seasons | Where-Object {
        (Get-ProviderId -Item $_ -Name 'Imdb') -or
        (Get-ProviderId -Item $_ -Name 'Tmdb') -or
        (Get-ProviderId -Item $_ -Name 'Tvdb')
    }).Count

Write-Information ''
Write-Information '  Episode field coverage (route A / route B prerequisites):'
foreach ($k in $epStats.Keys) {
    if ($k -eq 'Total') { continue }
    Write-Information ('    {0,-24} {1,6}  {2}' -f $k, $epStats[$k], (Format-Pct $epStats[$k] $epStats.Total))
}
Write-Information ('    {0,-24} {1,6}  {2}' -f 'SeasonsWithAnyProviderId', $seasonsWithAnyId, (Format-Pct $seasonsWithAnyId $seasons.Count))

# Which provider a Season carries matters: an Imdb-only resolver cannot use a Tvdb id,
# and IMDb has no season titles at all, so a Season imdb id would be surprising.
$seasonProviders = [ordered]@{ Imdb = 0; Tmdb = 0; Tvdb = 0 }
foreach ($s in $seasons) {
    foreach ($p in @('Imdb', 'Tmdb', 'Tvdb')) {
        if (Get-ProviderId -Item $s -Name $p) { $seasonProviders[$p]++ }
    }
}
Write-Information ''
Write-Information '  Season provider ids by kind:'
foreach ($p in $seasonProviders.Keys) {
    Write-Information ('    {0,-24} {1,6}  {2}' -f $p, $seasonProviders[$p], (Format-Pct $seasonProviders[$p] $seasons.Count))
}

Write-Section 'B) Dataset hit rate'

# Parent filter = every series in the library plus the control set.
$parentFilter = [Collections.Generic.HashSet[long]]::new()
foreach ($s in $byType['Series']) {
    $id = Get-ImdbNumericId (Get-ProviderId -Item $s -Name 'Imdb')
    if ($null -ne $id) { [void]$parentFilter.Add($id) }
}
foreach ($tt in $ControlSeries.Keys) {
    $id = Get-ImdbNumericId $tt
    if ($null -ne $id) { [void]$parentFilter.Add($id) }
}
Write-Information ('  parent filter: {0} series tconsts' -f $parentFilter.Count)
$epIndex = Import-EpisodeIndex -Path $episodePath -ParentFilter $parentFilter

Write-Information ''
Write-Information '  B1) library episodes'
$b1 = [ordered]@{
    RouteA_HasOwnId     = 0  # episode carries its own imdb id
    RouteA_Scored       = 0  # ...and that tconst has a rating row
    RouteA_NotAnEpisode = 0  # ...but that tconst is not a child row in title.episode
    RouteB_HasInputs    = 0  # series tconst + season + episode number all present
    RouteB_Mapped       = 0  # ...and the triple maps to a child tconst
    RouteB_Scored       = 0  # ...and that child has a rating
    BothResolved        = 0
    BothAgree           = 0
    NeitherResolved     = 0
}
$mismatches = [Collections.Generic.List[object]]::new()
$unresolved = [Collections.Generic.List[object]]::new()

foreach ($r in $epRows) {
    $aId = Get-ImdbNumericId $r.OwnImdb
    if ($null -ne $aId) {
        $b1.RouteA_HasOwnId++
        if ($ratings.Rating.ContainsKey($aId)) { $b1.RouteA_Scored++ }
        if (-not $epIndex.ByChild.ContainsKey($aId)) { $b1.RouteA_NotAnEpisode++ }
    }

    $bId = $null
    $parentId = Get-ImdbNumericId $r.SeriesImdb
    if ($null -ne $parentId -and $null -ne $r.Season -and $null -ne $r.Episode) {
        $b1.RouteB_HasInputs++
        $key = '{0}|{1}|{2}' -f $parentId, $r.Season, $r.Episode
        if ($epIndex.BySE.ContainsKey($key)) {
            $bId = $epIndex.BySE[$key]
            $b1.RouteB_Mapped++
            if ($ratings.Rating.ContainsKey($bId)) { $b1.RouteB_Scored++ }
        }
    }

    if ($null -ne $aId -and $null -ne $bId) {
        $b1.BothResolved++
        if ($aId -eq $bId) {
            $b1.BothAgree++
        }
        else {
            $mismatches.Add([pscustomobject]@{
                    Series  = $r.SeriesName
                    Episode = ('S{0:d2}E{1:d2} {2}' -f $r.Season, $r.Episode, $r.Name)
                    RouteA  = ('tt{0:d7}' -f $aId)
                    RouteB  = ('tt{0:d7}' -f $bId)
                })
        }
    }
    elseif ($null -eq $aId -and $null -eq $bId) {
        $b1.NeitherResolved++
        $unresolved.Add([pscustomobject]@{
                Series  = $r.SeriesName
                Episode = $r.Name
                Season  = $r.Season
                Number  = $r.Episode
                HasCr   = ($null -ne $r.Community)
            })
    }
}

foreach ($k in $b1.Keys) {
    Write-Information ('    {0,-20} {1,6}  {2}' -f $k, $b1[$k], (Format-Pct $b1[$k] $epRows.Count))
}
if ($mismatches.Count -gt 0) {
    Write-Information ''
    Write-Information '    Route A/B mismatches:'
    foreach ($m in $mismatches) {
        Write-Information ('      {0} | {1} | A={2} B={3}' -f $m.Series, $m.Episode, $m.RouteA, $m.RouteB)
    }
}
if ($unresolved.Count -gt 0) {
    Write-Information ''
    Write-Information ('    Unresolvable by either route ({0}), first 20:' -f $unresolved.Count)
    foreach ($u in ($unresolved | Select-Object -First 20)) {
        Write-Information ('      {0} | S{1}E{2} {3} | currentRating={4}' -f $u.Series, $u.Season, $u.Number, $u.Episode, $u.HasCr)
    }
}

Write-Information ''
Write-Information '  B2) control set (no library needed)'
$ctlRows = [Collections.Generic.List[object]]::new()
$ctlTotals = [ordered]@{ Episodes = 0; Scored = 0; Specials = 0; NoSE = 0; NoEpisodeRows = 0 }
$seasonCoverage = [Collections.Generic.List[object]]::new()

foreach ($tt in $ControlSeries.Keys) {
    $parentTt = Get-ImdbNumericId $tt
    if ($null -eq $parentTt) { continue }

    $children = @()
    if ($epIndex.ByParent.ContainsKey($parentTt)) { $children = $epIndex.ByParent[$parentTt] }

    $total = $children.Count
    $scored = 0; $specials = 0; $noSE = 0
    foreach ($childId in $children) {
        if ($ratings.Rating.ContainsKey($childId)) { $scored++ }
        $meta = $epIndex.ByChild[$childId]
        if ($null -eq $meta.Season -or $null -eq $meta.Episode) { $noSE++ }
        elseif ($meta.Season -eq 0) { $specials++ }
    }

    $ctlTotals.Episodes += $total
    $ctlTotals.Scored += $scored
    $ctlTotals.Specials += $specials
    $ctlTotals.NoSE += $noSE
    if ($total -eq 0) { $ctlTotals.NoEpisodeRows++ }

    # Per-season coverage feeds the minimum threshold for the season average.
    $perSeason = @{}
    foreach ($childId in $children) {
        $meta = $epIndex.ByChild[$childId]
        if ($null -eq $meta.Season) { continue }
        $sn = [int]$meta.Season
        if (-not $perSeason.ContainsKey($sn)) { $perSeason[$sn] = @{ Total = 0; Scored = 0 } }
        $perSeason[$sn].Total++
        if ($ratings.Rating.ContainsKey($childId)) { $perSeason[$sn].Scored++ }
    }
    foreach ($sn in $perSeason.Keys) {
        $t = $perSeason[$sn].Total
        if ($t -le 0) { continue }
        $pct = 100.0 * $perSeason[$sn].Scored / $t
        $seasonCoverage.Add([pscustomobject]@{
                Series = $ControlSeries[$tt]; Season = $sn; Total = $t
                Scored = $perSeason[$sn].Scored; Coverage = [math]::Round($pct, 1)
            })
    }

    $ctlRows.Add([pscustomobject]@{
            Series   = $ControlSeries[$tt]
            Tconst   = $tt
            Episodes = $total
            Scored   = $scored
            Coverage = $(if ($total -gt 0) { [math]::Round(100.0 * $scored / $total, 1) } else { 0 })
            NoSE     = $noSE
            Specials = $specials
        })
}

foreach ($row in ($ctlRows | Sort-Object Coverage)) {
    Write-Information ('    {0,-32} {1,5} eps {2,5} scored {3,6:N1}%  unnumbered={4,-5} s0={5}' -f `
            $row.Series, $row.Episodes, $row.Scored, $row.Coverage, $row.NoSE, $row.Specials)
}
$ctlPct = if ($ctlTotals.Episodes -gt 0) { 100.0 * $ctlTotals.Scored / $ctlTotals.Episodes } else { 0 }
Write-Information ('    {0,-32} {1,5} eps {2,5} scored {3,6:N1}%  unnumbered={4,-5} s0={5}' -f `
        'TOTAL', $ctlTotals.Episodes, $ctlTotals.Scored, $ctlPct, $ctlTotals.NoSE, $ctlTotals.Specials)

Write-Information ''
Write-Information '  Season coverage across the control set (drives the season-average threshold):'
$buckets = [ordered]@{ '100%' = 0; '90-99%' = 0; '75-89%' = 0; '50-74%' = 0; '<50%' = 0 }
foreach ($sc in $seasonCoverage) {
    if ($sc.Coverage -ge 100) { $buckets['100%']++ }
    elseif ($sc.Coverage -ge 90) { $buckets['90-99%']++ }
    elseif ($sc.Coverage -ge 75) { $buckets['75-89%']++ }
    elseif ($sc.Coverage -ge 50) { $buckets['50-74%']++ }
    else { $buckets['<50%']++ }
}
foreach ($b in $buckets.Keys) {
    Write-Information ('    {0,-8} {1,5} seasons  {2}' -f $b, $buckets[$b], (Format-Pct $buckets[$b] $seasonCoverage.Count))
}
$worst = $seasonCoverage | Where-Object { $_.Coverage -lt 90 } | Sort-Object Coverage | Select-Object -First 10
if ($worst) {
    Write-Information '    worst seasons:'
    foreach ($w in $worst) {
        Write-Information ('      {0,-32} S{1,-3} {2,3}/{3,-3} {4,6:N1}%' -f $w.Series, $w.Season, $w.Scored, $w.Total, $w.Coverage)
    }
}

# Known-answer check: Breaking Bad S5E14 "Ozymandias" against the raw dataset.
Write-Information ''
$ozyKey = '{0}|5|14' -f (Get-ImdbNumericId 'tt0903747')
if ($epIndex.BySE.ContainsKey($ozyKey)) {
    $ozy = $epIndex.BySE[$ozyKey]
    $ozyRating = $(if ($ratings.Rating.ContainsKey($ozy)) { $ratings.Rating[$ozy] } else { 'none' })
    Write-Information ('  known-answer check: Breaking Bad S05E14 -> tt{0:d7} rating {1}' -f $ozy, $ozyRating)
}
else {
    Write-Information '  known-answer check: FAILED - Breaking Bad S05E14 did not map'
}

Write-Section 'Report'
$reportPath = Join-Path $WorkDir 'diagnosis.json'
[pscustomobject]@{
    GeneratedUtc             = (Get-Date).ToUniversalTime().ToString('o')
    ServerUrl                = $ServerUrl
    LevelCounts              = [pscustomobject]@{
        Movie  = $byType['Movie'].Count
        Series = $byType['Series'].Count
        Season = $byType['Season'].Count
        Episode = $byType['Episode'].Count
    }
    VirtualCounts            = $virtualCount
    EpisodeStats             = $epStats
    SeasonsWithAnyProviderId = $seasonsWithAnyId
    SeasonProviderIdKinds    = $seasonProviders
    RouteStats               = $b1
    Mismatches               = $mismatches
    Unresolved               = $unresolved
    ControlSet               = $ctlRows
    ControlTotals            = $ctlTotals
    SeasonCoverage           = $seasonCoverage
    SeasonCoverageBuckets    = $buckets
    DuplicateSeKeys          = $epIndex.Ambiguous
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $reportPath -Encoding utf8

Write-Information ('  written: {0}' -f $reportPath)
Write-Information ('  datasets cached in: {0}' -f $datasetDir)

#endregion
