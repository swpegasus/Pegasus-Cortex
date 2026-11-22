@{
    SchemaVersion = 1
    # All badges overlay the unchanged CortexAppMark base. Coordinates use its 64-unit viewbox.
    Roles = @{
        Command = @{
            Label = 'Command host'
            Projects = @('PegasusCortex.App', 'Pegasus.AgentOps.ProcessHost')
            Lines = @(@{ Points = @(41, 42, 47, 48, 41, 54) }, @{ Points = @(49, 55, 56, 55) })
        }
        Pao = @{
            Label = 'PAO tools'
            Projects = @('pao')
            Lines = @(@{ Points = @(41, 56, 52, 45) }, @{ Points = @(50, 39, 50, 45, 56, 45, 56, 39) })
        }
        Service = @{
            Label = 'Execution service'
            Projects = @('Pegasus.AgentOps.Service', 'PegasusCortex.Service')
            Circles = @(@{ X = 49; Y = 49; Radius = 6 })
            Lines = @(
                @{ Points = @(49, 39, 49, 42) }, @{ Points = @(49, 56, 49, 59) },
                @{ Points = @(39, 49, 42, 49) }, @{ Points = @(56, 49, 59, 49) },
                @{ Points = @(42, 42, 44, 44) }, @{ Points = @(54, 54, 56, 56) },
                @{ Points = @(42, 56, 44, 54) }, @{ Points = @(54, 44, 56, 42) }
            )
        }
        Ui = @{
            Label = 'UI automation host'
            Projects = @('Pegasus.AgentOps.UiHost')
            Polygons = @(@{ Points = @(42, 39, 42, 56, 47, 51, 51, 58, 55, 55, 51, 49, 57, 47) })
        }
        Setup = @{
            Label = 'Setup and installer'
            Projects = @('PegasusCortex.Setup', 'PegasusCortex.InstallerBootstrap')
            Lines = @(@{ Points = @(49, 39, 49, 51) }, @{ Points = @(44, 47, 49, 52, 54, 47) },
                @{ Points = @(40, 52, 40, 57, 58, 57, 58, 52) })
        }
        Connection = @{
            Label = 'MCP connection'
            Projects = @('PegasusCortex.Mcp.Local', 'PegasusCortex.Mcp', 'PegasusCortex.Mcp.Integrated')
            Rectangles = @(@{ X = 43; Y = 44; Width = 12; Height = 8 })
            Lines = @(@{ Points = @(46, 39, 46, 44) }, @{ Points = @(52, 39, 52, 44) }, @{ Points = @(49, 52, 49, 58) })
        }
        Sync = @{
            Label = 'Synchronization'
            Projects = @('PegasusCortex.Sync.Client')
            Lines = @(@{ Points = @(41, 46, 41, 42, 56, 42) }, @{ Points = @(57, 52, 57, 56, 42, 56) })
            Polygons = @(@{ Points = @(53, 38, 59, 42, 53, 46) }, @{ Points = @(45, 52, 39, 56, 45, 60) })
        }
        Export = @{
            Label = 'Learning export'
            Projects = @('PegasusCortex.LearningExportCli')
            Lines = @(@{ Points = @(40, 47, 40, 57, 53, 57, 53, 53) },
                @{ Points = @(45, 51, 57, 39) }, @{ Points = @(49, 39, 57, 39, 57, 47) })
        }
        Archive = @{
            Label = 'Archive tool'
            Projects = @('PegasusCortex.ArchiveTool')
            Rectangles = @(@{ X = 40; Y = 41; Width = 18; Height = 6 }, @{ X = 42; Y = 47; Width = 14; Height = 10 })
            Lines = @(@{ Points = @(47, 51, 51, 51) })
        }
        Diagnostics = @{
            Label = 'Diagnostics'
            Projects = @('PegasusCortex.V2.Benchmarks', 'PegasusCortex.ContinuityMcpTestHost')
            Circles = @(@{ X = 46; Y = 46; Radius = 6 })
            Lines = @(@{ Points = @(50, 50, 58, 58) })
        }
        Library = @{
            Label = 'Visual library'
            Projects = @('VisualLibrary')
            Rectangles = @(@{ X = 40; Y = 41; Width = 18; Height = 16 })
            Lines = @(@{ Points = @(42, 54, 47, 49, 52, 54, 56, 50) })
            Circles = @(@{ X = 53; Y = 46; Radius = 1.5; Filled = $true })
        }
    }
}
