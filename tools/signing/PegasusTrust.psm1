Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-PegasusTrustTransition {
    param(
        [Parameter(Mandatory)][ValidateSet('Install', 'Uninstall')][string]$Action,
        [Parameter(Mandatory)][string]$ComponentId,
        [Parameter(Mandatory)][string]$RootThumbprint,
        [Parameter(Mandatory)][string]$PublisherThumbprint,
        [Parameter()][AllowNull()]$State,
        [Parameter(Mandatory)][bool]$RootPresent,
        [Parameter(Mandatory)][bool]$PublisherPresent
    )

    if ($ComponentId -cnotmatch '^pegasus\.[a-z0-9][a-z0-9.-]{1,63}$') {
        throw 'ComponentId is invalid.'
    }
    $components = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $rootOwned = $false
    $publisherOwned = $false
    if ($null -ne $State) {
        if ([string]$State.schema -cne 'pegasus.machine-trust-ownership/1.0' -or
            [string]$State.channel -cne 'Prod Alpha' -or
            [string]$State.rootThumbprint -cne $RootThumbprint -or
            [string]$State.publisherThumbprint -cne $PublisherThumbprint) {
            throw 'The existing Pegasus trust ownership state does not match this pinned identity.'
        }
        $rootOwned = [bool]$State.rootInstalledByPegasus
        $publisherOwned = [bool]$State.publisherInstalledByPegasus
        foreach ($component in @($State.components)) {
            if ([string]$component -cnotmatch '^pegasus\.[a-z0-9][a-z0-9.-]{1,63}$' -or
                -not $components.Add([string]$component)) {
                throw 'The existing Pegasus trust ownership component set is invalid.'
            }
        }
    }

    if ($Action -ceq 'Install') {
        if ($rootOwned -and -not $RootPresent) { throw 'Pegasus-owned root trust is unexpectedly absent.' }
        if ($publisherOwned -and -not $PublisherPresent) { throw 'Pegasus-owned publisher trust is unexpectedly absent.' }
        $addRoot = -not $RootPresent
        $addPublisher = -not $PublisherPresent
        $rootOwned = $rootOwned -or $addRoot
        $publisherOwned = $publisherOwned -or $addPublisher
        $null = $components.Add($ComponentId)
        $ordered = @($components); [Array]::Sort($ordered, [StringComparer]::Ordinal)
        return [pscustomobject]@{
            AddRoot = $addRoot
            AddPublisher = $addPublisher
            RemoveRoot = $false
            RemovePublisher = $false
            NextState = [ordered]@{
                schema = 'pegasus.machine-trust-ownership/1.0'
                channel = 'Prod Alpha'
                rootThumbprint = $RootThumbprint
                publisherThumbprint = $PublisherThumbprint
                rootInstalledByPegasus = $rootOwned
                publisherInstalledByPegasus = $publisherOwned
                components = $ordered
            }
        }
    }

    $null = $components.Remove($ComponentId)
    if ($components.Count -gt 0) {
        $ordered = @($components); [Array]::Sort($ordered, [StringComparer]::Ordinal)
        return [pscustomobject]@{
            AddRoot = $false; AddPublisher = $false; RemoveRoot = $false; RemovePublisher = $false
            NextState = [ordered]@{
                schema = 'pegasus.machine-trust-ownership/1.0'; channel = 'Prod Alpha'
                rootThumbprint = $RootThumbprint; publisherThumbprint = $PublisherThumbprint
                rootInstalledByPegasus = $rootOwned; publisherInstalledByPegasus = $publisherOwned
                components = $ordered
            }
        }
    }
    return [pscustomobject]@{
        AddRoot = $false
        AddPublisher = $false
        RemoveRoot = $rootOwned -and $RootPresent
        RemovePublisher = $publisherOwned -and $PublisherPresent
        NextState = $null
    }
}

Export-ModuleMember -Function 'Get-PegasusTrustTransition'
