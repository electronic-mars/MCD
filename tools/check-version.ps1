<#
.SYNOPSIS
Fails when the git tag and Directory.Version.props disagree.

.DESCRIPTION
The version is typed in exactly one place. This is what stops a release built
from tag v1.2.0 shipping a binary that calls itself 1.1.0 - a mismatch nobody
notices until a user reports a bug against a version that was never built.
#>
[CmdletBinding()]
param(
    # Only a tag is worth comparing. On a push to a branch GITHUB_REF_NAME is
    # the branch name, and checking "main" against a version number fails every
    # build on the branch for no reason.
    [string]$Tag = $(if ($env:GITHUB_REF_TYPE -eq 'tag') { $env:GITHUB_REF_NAME } else { '' })
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$props = Join-Path $root 'Directory.Version.props'

$match = Select-String -Path $props -Pattern '<McdVersion>([^<]+)</McdVersion>'
if (-not $match) { throw "no <McdVersion> in $props" }
$declared = $match.Matches[0].Groups[1].Value

if (-not $Tag) {
    Write-Host "Directory.Version.props says $declared (no tag to compare against)"
    exit 0
}

$wanted = $Tag -replace '^v', ''
if ($wanted -ne $declared) {
    throw "the tag says $wanted and Directory.Version.props says $declared"
}

Write-Host "version $declared matches tag $Tag"
