$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$out = Join-Path $PSScriptRoot 'build'
$sdk = '1.0.4314.35'
$package = Join-Path $out "webview2-$sdk.zip"
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
New-Item -ItemType Directory -Force -Path $out | Out-Null

# Use the installed WebView2 runtime. Only the pinned SDK loader and assemblies ship beside the app.
if (!(Test-Path $package)) {
    $url = "https://api.nuget.org/v3-flatcontainer/microsoft.web.webview2/$sdk/microsoft.web.webview2.$sdk.nupkg"
    Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile ($package + '.tmp')
    Move-Item -LiteralPath ($package + '.tmp') -Destination $package -Force
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($package)
try {
    foreach ($entry in @('lib/net462/Microsoft.Web.WebView2.Core.dll', 'lib/net462/Microsoft.Web.WebView2.WinForms.dll', 'runtimes/win-x64/native/WebView2Loader.dll')) {
        [IO.Compression.ZipFileExtensions]::ExtractToFile($archive.GetEntry($entry), (Join-Path $out ([IO.Path]::GetFileName($entry))), $true)
    }
} finally { $archive.Dispose() }

$flags = @('/nologo', '/platform:x64', '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', '/r:System.Web.dll', '/r:System.Web.Extensions.dll', '/r:System.Net.Http.dll', '/r:System.Xml.Linq.dll')
$flags += '/r:' + (Join-Path $out 'Microsoft.Web.WebView2.Core.dll')
$flags += '/r:' + (Join-Path $out 'Microsoft.Web.WebView2.WinForms.dll')
$flags += '/resource:' + (Join-Path $PSScriptRoot 'collector.js') + ',collector.js'
$flags += '/resource:' + (Join-Path $PSScriptRoot 'article-body.js') + ',article-body.js'
$flags += '/resource:' + (Join-Path $PSScriptRoot 'inbox.js') + ',inbox.js'
$flags += '/resource:' + (Join-Path $PSScriptRoot 'inbox.css') + ',inbox.css'
# Ship the original font files and their license notices with the app.
foreach ($asset in @('InterVariable.woff2','Merriweather-Regular.ttf','Inter-OFL.txt','Merriweather-OFL.txt')) {
    $flags += '/resource:' + (Join-Path $PSScriptRoot "fonts\$asset") + ',' + $asset
}
$shared = @('AppConfig.cs', 'Store.cs', 'Collector.cs', 'RssCollector.cs', 'Inbox.cs', 'ArticleBody.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }

# Use the compiler already supplied by .NET Framework; no system-wide SDK or service is installed.
& $compiler @flags '/target:winexe' ('/out:' + (Join-Path $out 'FeedInbox.exe')) @shared (Join-Path $PSScriptRoot 'App.cs')
if ($LASTEXITCODE -ne 0) { throw 'Feed Inbox could not compile.' }
& $compiler @flags '/target:exe' ('/out:' + (Join-Path $out 'Tests.exe')) @shared (Join-Path $PSScriptRoot 'Tests.cs')
if ($LASTEXITCODE -ne 0) { throw 'The inbox tests could not compile.' }
& (Join-Path $out 'Tests.exe')
if ($LASTEXITCODE -ne 0) { throw 'The inbox tests failed.' }

# An optional example config should never overwrite settings in an existing build directory.
$config = Join-Path $out 'feed-inbox.conf'
if (!(Test-Path -LiteralPath $config)) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'feed-inbox.conf') -Destination $config }
