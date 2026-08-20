param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $Version,

    [string] $PackagesDirectory = 'packages'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$packagesPath = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $PackagesDirectory))
$stagingRoot = Join-Path $repositoryRoot "artifacts/sbom-drops/$Version"
$validationRoot = Join-Path $repositoryRoot "artifacts/sbom-validation/$Version"

$packageDefinitions = @(
    @{
        Name = 'PeerSharp'
        AssetsPath = 'src/PeerSharp/obj/project.assets.json'
        Description = 'A high-performance BitTorrent engine for .NET.'
        PackagedProjectReferences = @()
    },
    @{
        Name = 'PeerSharp.WebTorrent'
        AssetsPath = 'src/PeerSharp.WebTorrent/obj/project.assets.json'
        Description = 'Optional WebTorrent over WebRTC support for PeerSharp.'
        PackagedProjectReferences = @(
            @{
                Name = 'PeerSharp'
                Version = $Version
            }
        )
    }
)

[System.IO.Directory]::CreateDirectory($stagingRoot) | Out-Null
[System.IO.Directory]::CreateDirectory($validationRoot) | Out-Null

foreach ($definition in $packageDefinitions) {
    $packageName = $definition.Name
    $dropPath = Join-Path $stagingRoot $packageName
    $componentPath = Join-Path $stagingRoot "$packageName-components"
    [System.IO.Directory]::CreateDirectory($dropPath) | Out-Null
    [System.IO.Directory]::CreateDirectory($componentPath) | Out-Null

    $nugetPackage = Join-Path $packagesPath "$packageName.$Version.nupkg"
    $symbolPackage = Join-Path $packagesPath "$packageName.$Version.snupkg"
    if (!(Test-Path -LiteralPath $nugetPackage -PathType Leaf)) {
        throw "NuGet package not found: $nugetPackage"
    }
    if (!(Test-Path -LiteralPath $symbolPackage -PathType Leaf)) {
        throw "Symbol package not found: $symbolPackage"
    }

    Copy-Item -LiteralPath $nugetPackage, $symbolPackage -Destination $dropPath -Force

    # Scan a release-specific copy of project.assets.json. Pointing Component Detection at the
    # project directory also scans stale .nuspec files under obj on long-lived checkouts and can
    # incorrectly report old PeerSharp releases as dependencies.
    $assetsPath = Join-Path $repositoryRoot $definition.AssetsPath
    if (!(Test-Path -LiteralPath $assetsPath -PathType Leaf)) {
        throw "Restore assets not found: $assetsPath"
    }
    $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json -AsHashtable

    # dotnet pack turns these ProjectReferences into NuGet dependencies in the shipped package.
    # Normalize the restore graph to that packaged form so Component Detection includes the edge.
    foreach ($reference in $definition.PackagedProjectReferences) {
        $referenceKey = "$($reference.Name)/$($reference.Version)"
        if (!$assets.libraries.ContainsKey($referenceKey)) {
            throw "Project reference $referenceKey was not found in $assetsPath"
        }

        $assets.libraries[$referenceKey].type = 'package'
        $assets.libraries[$referenceKey].path = "$($reference.Name.ToLowerInvariant())/$($reference.Version)"
        $assets.libraries[$referenceKey].Remove('msbuildProject') | Out-Null
        foreach ($target in $assets.targets.Values) {
            if ($target.ContainsKey($referenceKey)) {
                $target[$referenceKey].type = 'package'
            }
        }
        foreach ($framework in $assets.project.frameworks.Values) {
            $framework.dependencies[$reference.Name] = @{
                target = 'Package'
                version = "[$($reference.Version), )"
            }
        }
    }

    # Drop build-only packages before scanning. Component Detection reports every package in the
    # restore graph, but analyzers and build tasks are not part of the shipped artifact: PeerSharp's
    # nuspec declares one dependency while the unfiltered graph yields four. Listing SonarAnalyzer
    # and ILLink.Tasks as components of the release makes the SBOM wrong in the direction that costs
    # its consumers real work, because a vulnerability or licence scan cannot tell that they never
    # ship. PrivateAssets="all" is what marks them, and it reaches the restore graph as
    # suppressParent="All".
    $roots = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($framework in $assets.project.frameworks.Values) {
        foreach ($name in @($framework.dependencies.Keys)) {
            if ($framework.dependencies[$name].suppressParent -eq 'All') {
                $framework.dependencies.Remove($name) | Out-Null
            }
            else {
                $roots.Add($name) | Out-Null
            }
        }
    }

    # A package that only the dropped ones pulled in is not shipped either, so keep just what is
    # still reachable from a surviving top-level dependency.
    $keep = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($target in $assets.targets.Values) {
        $pending = [System.Collections.Generic.Queue[string]]::new()
        foreach ($key in $target.Keys) {
            if ($roots.Contains(($key -split '/')[0])) {
                $pending.Enqueue($key)
            }
        }

        while ($pending.Count -gt 0) {
            $key = $pending.Dequeue()
            if (!$keep.Add($key)) {
                continue
            }

            foreach ($dependency in @($target[$key].dependencies.Keys)) {
                foreach ($candidate in $target.Keys) {
                    if (($candidate -split '/')[0] -eq $dependency) {
                        $pending.Enqueue($candidate)
                    }
                }
            }
        }
    }

    foreach ($target in $assets.targets.Values) {
        foreach ($key in @($target.Keys)) {
            if (!$keep.Contains($key)) {
                $target.Remove($key) | Out-Null
            }
        }
    }

    foreach ($key in @($assets.libraries.Keys)) {
        if (!$keep.Contains($key)) {
            $assets.libraries.Remove($key) | Out-Null
        }
    }

    $componentAssets = Join-Path $componentPath 'project.assets.json'
    $assets | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $componentAssets -Encoding utf8

    dotnet tool run sbom-tool generate `
        -b $dropPath `
        -bc $componentPath `
        -pn $packageName `
        -pv $Version `
        -ps Peerfluence `
        -nsb 'https://github.com/ligenq/PeerSharp/sbom' `
        -nsu 'release' `
        -mi 'SPDX:2.2' `
        -D true `
        -pm true `
        -V Warning
    if ($LASTEXITCODE -ne 0) {
        throw "SBOM generation failed for $packageName."
    }

    $manifest = Join-Path $dropPath '_manifest/spdx_2.2/manifest.spdx.json'
    $sbom = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
    $rootPackages = @($sbom.packages | Where-Object SPDXID -eq 'SPDXRef-RootPackage')
    if ($rootPackages.Count -ne 1) {
        throw "Expected one root package in the $packageName SBOM, found $($rootPackages.Count)."
    }

    $rootPackage = $rootPackages[0]
    $staleSelfPackages = @($sbom.packages | Where-Object {
        $_.name -eq $packageName -and $_.versionInfo -ne $Version
    })
    if ($staleSelfPackages.Count -gt 0) {
        $staleVersions = ($staleSelfPackages.versionInfo | Sort-Object -Unique) -join ', '
        throw "The $packageName SBOM contains stale versions: $staleVersions"
    }

    $rootPackage.licenseDeclared = 'MIT'
    $rootPackage.copyrightText = 'Copyright (c) 2026 ligenq'
    $rootPackage.downloadLocation = "https://www.nuget.org/packages/$packageName/$Version"
    $rootPackage | Add-Member -NotePropertyName description -NotePropertyValue $definition.Description -Force
    $rootPurls = @($rootPackage.externalRefs | Where-Object referenceType -eq 'purl')
    if ($rootPurls.Count -ne 1) {
        throw "Expected one package URL for $packageName, found $($rootPurls.Count)."
    }
    $rootPurls[0].referenceLocator = "pkg:nuget/$packageName@$Version"

    foreach ($reference in $definition.PackagedProjectReferences) {
        $dependencies = @($sbom.packages | Where-Object {
            $_.name -eq $reference.Name -and $_.versionInfo -eq $reference.Version
        })
        if ($dependencies.Count -ne 1) {
            throw "Expected one $($reference.Name) $($reference.Version) dependency in the $packageName SBOM, found $($dependencies.Count)."
        }

        $dependency = $dependencies[0]
        $dependencyRelationships = @($sbom.relationships | Where-Object {
            $_.spdxElementId -eq 'SPDXRef-RootPackage' -and
            $_.relationshipType -eq 'DEPENDS_ON' -and
            $_.relatedSpdxElement -eq $dependency.SPDXID
        })
        if ($dependencyRelationships.Count -ne 1) {
            throw "Expected $packageName to depend on $($reference.Name) $($reference.Version) exactly once in the SBOM."
        }

        $dependency.supplier = 'Organization: Peerfluence'
        $dependency.licenseDeclared = 'MIT'
        $dependency.copyrightText = 'Copyright (c) 2026 ligenq'
        $dependency.downloadLocation = "https://www.nuget.org/packages/$($reference.Name)/$($reference.Version)"
    }

    $sbom | Add-Member -NotePropertyName documentComment `
        -NotePropertyValue 'Build-time SBOM generated from the release restore graph and packaged artifacts.' `
        -Force
    $sbom | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $manifest -Encoding utf8

    $validationOutput = Join-Path $validationRoot "$packageName.json"
    dotnet tool run sbom-tool validate `
        -b $dropPath `
        -o $validationOutput `
        -mi 'SPDX:2.2' `
        -n true `
        -V Warning
    if ($LASTEXITCODE -ne 0) {
        throw "SBOM validation failed for $packageName."
    }

    $releaseSbom = Join-Path $packagesPath "$packageName.$Version.spdx.json"
    Copy-Item -LiteralPath $manifest -Destination $releaseSbom -Force
    Write-Host "Generated and validated $releaseSbom"
}
