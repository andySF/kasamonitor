using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KasaMonitor.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ApplianceRuns",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SignatureId = table.Column<long>(type: "bigint", nullable: false),
                    StartTs = table.Column<long>(type: "bigint", nullable: false),
                    EndTs = table.Column<long>(type: "bigint", nullable: true),
                    PowerW = table.Column<double>(type: "float", nullable: false),
                    EstimatedWh = table.Column<double>(type: "float", nullable: true),
                    StartEventId = table.Column<long>(type: "bigint", nullable: false),
                    EndEventId = table.Column<long>(type: "bigint", nullable: true),
                    HouseBeforeW = table.Column<double>(type: "float", nullable: false),
                    EndReason = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApplianceRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ApplianceSignatures",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Icon = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Category = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    MeanDeltaP = table.Column<double>(type: "float", nullable: false),
                    MeanDeltaQ = table.Column<double>(type: "float", nullable: false),
                    MeanTransient = table.Column<double>(type: "float", nullable: false),
                    Count = table.Column<int>(type: "int", nullable: false),
                    LastSeen = table.Column<long>(type: "bigint", nullable: true),
                    LabelSource = table.Column<int>(type: "int", nullable: false),
                    PlugId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Hidden = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApplianceSignatures", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ClimateDaily",
                columns: table => new
                {
                    SensorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Day = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    MinC = table.Column<double>(type: "float", nullable: true),
                    AvgC = table.Column<double>(type: "float", nullable: true),
                    MaxC = table.Column<double>(type: "float", nullable: true),
                    MinHumidity = table.Column<double>(type: "float", nullable: true),
                    AvgHumidity = table.Column<double>(type: "float", nullable: true),
                    MaxHumidity = table.Column<double>(type: "float", nullable: true),
                    Count = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClimateDaily", x => new { x.SensorId, x.Day });
                });

            migrationBuilder.CreateTable(
                name: "ClimateReadings",
                columns: table => new
                {
                    SensorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ts = table.Column<long>(type: "bigint", nullable: false),
                    TemperatureC = table.Column<double>(type: "float", nullable: false),
                    HumidityPercent = table.Column<double>(type: "float", nullable: true),
                    BatteryPercent = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClimateReadings", x => new { x.SensorId, x.Ts });
                });

            migrationBuilder.CreateTable(
                name: "ClimateSensors",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ExternalId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Room = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    IsOutdoor = table.Column<bool>(type: "bit", nullable: false),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false),
                    LastSeenUtc = table.Column<long>(type: "bigint", nullable: true),
                    BatteryPercent = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClimateSensors", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DailyEnergy",
                columns: table => new
                {
                    PlugId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Date = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    EnergyWh = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DailyEnergy", x => new { x.PlugId, x.Date });
                });

            migrationBuilder.CreateTable(
                name: "EntityPrefs",
                columns: table => new
                {
                    Key = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Visible = table.Column<bool>(type: "bit", nullable: true),
                    InHistory = table.Column<bool>(type: "bit", nullable: true),
                    InMetrics = table.Column<bool>(type: "bit", nullable: true),
                    NameOverride = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PrecisionOverride = table.Column<int>(type: "int", nullable: true),
                    ClassOverride = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    UnitOverride = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    CommandsEnabled = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EntityPrefs", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "GenericDevices",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Integration = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ExternalId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    NameIsCustom = table.Column<bool>(type: "bit", nullable: false),
                    Room = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Manufacturer = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Model = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Firmware = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ViaDevice = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    AutoCategory = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CategoryOverride = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false),
                    FirstSeenUtc = table.Column<long>(type: "bigint", nullable: false),
                    LastSeenUtc = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GenericDevices", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GenericEntities",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DeviceId = table.Column<long>(type: "bigint", nullable: false),
                    Integration = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ExternalId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Component = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    DataType = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Class = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ClassRaw = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Unit = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    StateClass = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    Precision = table.Column<int>(type: "int", nullable: true),
                    Category = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Writable = table.Column<bool>(type: "bit", nullable: false),
                    Problem = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    StateClassInferred = table.Column<bool>(type: "bit", nullable: false),
                    DiscoveryJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsRemoved = table.Column<bool>(type: "bit", nullable: false),
                    LastNumber = table.Column<double>(type: "float", nullable: true),
                    LastText = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    LastTs = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GenericEntities", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GenericHourly",
                columns: table => new
                {
                    EntityId = table.Column<long>(type: "bigint", nullable: false),
                    HourTs = table.Column<long>(type: "bigint", nullable: false),
                    Min = table.Column<double>(type: "float", nullable: true),
                    Avg = table.Column<double>(type: "float", nullable: true),
                    Max = table.Column<double>(type: "float", nullable: true),
                    Last = table.Column<double>(type: "float", nullable: true),
                    Count = table.Column<int>(type: "int", nullable: false),
                    Delta = table.Column<double>(type: "float", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GenericHourly", x => new { x.EntityId, x.HourTs });
                });

            migrationBuilder.CreateTable(
                name: "GenericReadings",
                columns: table => new
                {
                    EntityId = table.Column<long>(type: "bigint", nullable: false),
                    Ts = table.Column<long>(type: "bigint", nullable: false),
                    Value = table.Column<double>(type: "float", nullable: true),
                    Text = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GenericReadings", x => new { x.EntityId, x.Ts });
                });

            migrationBuilder.CreateTable(
                name: "HeatingDevices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    InstallationId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    GatewaySerial = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    DeviceId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ModelId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    LastSeenUtc = table.Column<long>(type: "bigint", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HeatingDevices", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "HeatingEnergyDaily",
                columns: table => new
                {
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Day = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    GasHeatingMaxM3 = table.Column<double>(type: "float", nullable: true),
                    GasHeatingLastM3 = table.Column<double>(type: "float", nullable: true),
                    GasDhwMaxM3 = table.Column<double>(type: "float", nullable: true),
                    GasDhwLastM3 = table.Column<double>(type: "float", nullable: true),
                    HeatHeatingKwh = table.Column<double>(type: "float", nullable: true),
                    HeatDhwKwh = table.Column<double>(type: "float", nullable: true),
                    PowerHeatingKwh = table.Column<double>(type: "float", nullable: true),
                    PowerDhwKwh = table.Column<double>(type: "float", nullable: true),
                    Source = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    UpdatedTs = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HeatingEnergyDaily", x => new { x.DeviceId, x.Day });
                });

            migrationBuilder.CreateTable(
                name: "HeatingEnergyPeriods",
                columns: table => new
                {
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Purpose = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Period = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Value = table.Column<double>(type: "float", nullable: false),
                    Unit = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    UpdatedTs = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HeatingEnergyPeriods", x => new { x.DeviceId, x.Kind, x.Purpose, x.Period });
                });

            migrationBuilder.CreateTable(
                name: "HeatingRawFeatures",
                columns: table => new
                {
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ts = table.Column<long>(type: "bigint", nullable: false),
                    Json = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HeatingRawFeatures", x => x.DeviceId);
                });

            migrationBuilder.CreateTable(
                name: "HeatingReadings",
                columns: table => new
                {
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ts = table.Column<long>(type: "bigint", nullable: false),
                    BoilerTempC = table.Column<double>(type: "float", nullable: true),
                    BoilerTargetC = table.Column<double>(type: "float", nullable: true),
                    SupplyTempC = table.Column<double>(type: "float", nullable: true),
                    ReturnTempC = table.Column<double>(type: "float", nullable: true),
                    OutsideTempC = table.Column<double>(type: "float", nullable: true),
                    RoomTempC = table.Column<double>(type: "float", nullable: true),
                    DhwTempC = table.Column<double>(type: "float", nullable: true),
                    DhwTargetC = table.Column<double>(type: "float", nullable: true),
                    BurnerActive = table.Column<bool>(type: "bit", nullable: true),
                    ModulationPct = table.Column<double>(type: "float", nullable: true),
                    PumpPct = table.Column<double>(type: "float", nullable: true),
                    PressureBar = table.Column<double>(type: "float", nullable: true),
                    BurnerStarts = table.Column<long>(type: "bigint", nullable: true),
                    BurnerHours = table.Column<double>(type: "float", nullable: true),
                    OperatingMode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ActiveProgram = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    DhwMode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ActiveError = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    StatusCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    GasHeatingTodayM3 = table.Column<double>(type: "float", nullable: true),
                    GasDhwTodayM3 = table.Column<double>(type: "float", nullable: true),
                    PowerTodayKwh = table.Column<double>(type: "float", nullable: true),
                    HeatTodayKwh = table.Column<double>(type: "float", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HeatingReadings", x => new { x.DeviceId, x.Ts });
                });

            migrationBuilder.CreateTable(
                name: "LiveMinuteAggregates",
                columns: table => new
                {
                    PlugId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MinuteTs = table.Column<long>(type: "bigint", nullable: false),
                    MinW = table.Column<double>(type: "float", nullable: false),
                    AvgW = table.Column<double>(type: "float", nullable: false),
                    MaxW = table.Column<double>(type: "float", nullable: false),
                    AvgVoltageV = table.Column<double>(type: "float", nullable: true),
                    EnergyWh = table.Column<double>(type: "float", nullable: false),
                    SampleCount = table.Column<int>(type: "int", nullable: false),
                    CoveredSeconds = table.Column<double>(type: "float", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LiveMinuteAggregates", x => new { x.PlugId, x.MinuteTs });
                });

            migrationBuilder.CreateTable(
                name: "LiveSamples",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PlugId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ts = table.Column<long>(type: "bigint", nullable: false),
                    PowerW = table.Column<double>(type: "float", nullable: false),
                    ApparentVa = table.Column<double>(type: "float", nullable: true),
                    VoltageV = table.Column<double>(type: "float", nullable: true),
                    CurrentA = table.Column<double>(type: "float", nullable: true),
                    Pf = table.Column<double>(type: "float", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LiveSamples", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MeterEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PlugId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ts = table.Column<long>(type: "bigint", nullable: false),
                    DeltaP = table.Column<double>(type: "float", nullable: false),
                    DeltaQ = table.Column<double>(type: "float", nullable: false),
                    DeltaPf = table.Column<double>(type: "float", nullable: false),
                    TransientRatio = table.Column<double>(type: "float", nullable: false),
                    RiseSeconds = table.Column<double>(type: "float", nullable: false),
                    PowerBeforeW = table.Column<double>(type: "float", nullable: false),
                    SignatureId = table.Column<long>(type: "bigint", nullable: true),
                    Label = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Source = table.Column<int>(type: "int", nullable: false),
                    LabelPlugId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MeterEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MieleCycles",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DeviceId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    StartTs = table.Column<long>(type: "bigint", nullable: false),
                    EndTs = table.Column<long>(type: "bigint", nullable: true),
                    LastRunningTs = table.Column<long>(type: "bigint", nullable: false),
                    ProgramRaw = table.Column<int>(type: "int", nullable: true),
                    Program = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PhasesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    EnergyKwh = table.Column<double>(type: "float", nullable: true),
                    WaterL = table.Column<double>(type: "float", nullable: true),
                    PlugEnergyWh = table.Column<double>(type: "float", nullable: true),
                    EndReason = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    Source = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ApiStartTs = table.Column<long>(type: "bigint", nullable: true),
                    ApiEndTs = table.Column<long>(type: "bigint", nullable: true),
                    ApiEnergyKwh = table.Column<double>(type: "float", nullable: true),
                    ApiWaterL = table.Column<double>(type: "float", nullable: true),
                    EnergyProfileJson = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MieleCycles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MieleDevices",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    TypeRaw = table.Column<int>(type: "int", nullable: true),
                    TypeText = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    TechType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    DeviceName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Alias = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    PlugId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false),
                    LastSeenUtc = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MieleDevices", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MieleStateSnapshots",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Ts = table.Column<long>(type: "bigint", nullable: false),
                    DeviceId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    StatusRaw = table.Column<int>(type: "int", nullable: true),
                    StatusText = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ProgramTypeRaw = table.Column<int>(type: "int", nullable: true),
                    ProgramTypeText = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ProgramIdRaw = table.Column<int>(type: "int", nullable: true),
                    ProgramIdText = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PhaseRaw = table.Column<int>(type: "int", nullable: true),
                    PhaseText = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    RemainingSec = table.Column<int>(type: "int", nullable: true),
                    ElapsedSec = table.Column<int>(type: "int", nullable: true),
                    StartTimeSec = table.Column<int>(type: "int", nullable: true),
                    TargetTempC = table.Column<double>(type: "float", nullable: true),
                    SpinRpm = table.Column<int>(type: "int", nullable: true),
                    DryingStepRaw = table.Column<int>(type: "int", nullable: true),
                    DoorOpen = table.Column<bool>(type: "bit", nullable: true),
                    SignalFailure = table.Column<bool>(type: "bit", nullable: true),
                    SignalInfo = table.Column<bool>(type: "bit", nullable: true),
                    EcoEnergyKwh = table.Column<double>(type: "float", nullable: true),
                    EcoWaterL = table.Column<double>(type: "float", nullable: true),
                    EcoEnergyForecast = table.Column<double>(type: "float", nullable: true),
                    EcoWaterForecast = table.Column<double>(type: "float", nullable: true),
                    RemoteFullControl = table.Column<bool>(type: "bit", nullable: true),
                    RemoteSmartGrid = table.Column<bool>(type: "bit", nullable: true),
                    RemoteMobileStart = table.Column<bool>(type: "bit", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MieleStateSnapshots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MonthlyEnergy",
                columns: table => new
                {
                    PlugId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Year = table.Column<int>(type: "int", nullable: false),
                    Month = table.Column<int>(type: "int", nullable: false),
                    EnergyWh = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MonthlyEnergy", x => new { x.PlugId, x.Year, x.Month });
                });

            migrationBuilder.CreateTable(
                name: "OneHourAggregates",
                columns: table => new
                {
                    PlugId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BucketStartUtc = table.Column<long>(type: "bigint", nullable: false),
                    PAvgMw = table.Column<long>(type: "bigint", nullable: false),
                    PMaxMw = table.Column<long>(type: "bigint", nullable: false),
                    EnergyWh = table.Column<long>(type: "bigint", nullable: false),
                    SampleCount = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OneHourAggregates", x => new { x.PlugId, x.BucketStartUtc });
                });

            migrationBuilder.CreateTable(
                name: "OneMinuteAggregates",
                columns: table => new
                {
                    PlugId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BucketStartUtc = table.Column<long>(type: "bigint", nullable: false),
                    PAvgMw = table.Column<long>(type: "bigint", nullable: false),
                    PMaxMw = table.Column<long>(type: "bigint", nullable: false),
                    PMinMw = table.Column<long>(type: "bigint", nullable: false),
                    SampleCount = table.Column<int>(type: "int", nullable: false),
                    OnlineCount = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OneMinuteAggregates", x => new { x.PlugId, x.BucketStartUtc });
                });

            migrationBuilder.CreateTable(
                name: "Plugs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Mac = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    IpAddress = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Alias = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    AliasIsCustom = table.Column<bool>(type: "bit", nullable: false),
                    Model = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    HwVer = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SwVer = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    OemId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    FirstSeenUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastSeenUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Protocol = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false, defaultValue: "legacy"),
                    UnsupportedReason = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Channel = table.Column<int>(type: "int", nullable: false),
                    Role = table.Column<int>(type: "int", nullable: false),
                    StreamEnabled = table.Column<bool>(type: "bit", nullable: false),
                    NilmIgnored = table.Column<int>(type: "int", nullable: false),
                    SingleAppliance = table.Column<bool>(type: "bit", nullable: false),
                    ApplianceName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ApplianceCategory = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Plugs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RelayCommands",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Ts = table.Column<long>(type: "bigint", nullable: false),
                    PlugId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    On = table.Column<bool>(type: "bit", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Result = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RelayCommands", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Samples",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PlugId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TimestampUtc = table.Column<long>(type: "bigint", nullable: false),
                    Online = table.Column<bool>(type: "bit", nullable: false),
                    PowerMw = table.Column<long>(type: "bigint", nullable: true),
                    VoltageMv = table.Column<long>(type: "bigint", nullable: true),
                    CurrentMa = table.Column<long>(type: "bigint", nullable: true),
                    TotalWh = table.Column<long>(type: "bigint", nullable: true),
                    Rssi = table.Column<int>(type: "int", nullable: true),
                    RelayOn = table.Column<bool>(type: "bit", nullable: true),
                    ErrorMessage = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Samples", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Settings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Value = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Key);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApplianceRuns_EndTs",
                table: "ApplianceRuns",
                column: "EndTs");

            migrationBuilder.CreateIndex(
                name: "IX_ApplianceRuns_SignatureId",
                table: "ApplianceRuns",
                column: "SignatureId");

            migrationBuilder.CreateIndex(
                name: "IX_ApplianceRuns_StartTs",
                table: "ApplianceRuns",
                column: "StartTs");

            migrationBuilder.CreateIndex(
                name: "IX_ClimateReadings_Ts",
                table: "ClimateReadings",
                column: "Ts");

            migrationBuilder.CreateIndex(
                name: "IX_ClimateSensors_Source_ExternalId",
                table: "ClimateSensors",
                columns: new[] { "Source", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GenericDevices_Integration_ExternalId",
                table: "GenericDevices",
                columns: new[] { "Integration", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GenericEntities_DeviceId",
                table: "GenericEntities",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_GenericEntities_Integration_ExternalId",
                table: "GenericEntities",
                columns: new[] { "Integration", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HeatingDevices_Provider_InstallationId_GatewaySerial_DeviceId",
                table: "HeatingDevices",
                columns: new[] { "Provider", "InstallationId", "GatewaySerial", "DeviceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LiveSamples_PlugId_Ts",
                table: "LiveSamples",
                columns: new[] { "PlugId", "Ts" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MeterEvents_PlugId_Ts",
                table: "MeterEvents",
                columns: new[] { "PlugId", "Ts" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MeterEvents_SignatureId_Ts",
                table: "MeterEvents",
                columns: new[] { "SignatureId", "Ts" });

            migrationBuilder.CreateIndex(
                name: "IX_MeterEvents_Ts",
                table: "MeterEvents",
                column: "Ts");

            migrationBuilder.CreateIndex(
                name: "IX_MieleCycles_DeviceId_ApiStartTs",
                table: "MieleCycles",
                columns: new[] { "DeviceId", "ApiStartTs" },
                unique: true,
                filter: "[ApiStartTs] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_MieleCycles_DeviceId_StartTs",
                table: "MieleCycles",
                columns: new[] { "DeviceId", "StartTs" });

            migrationBuilder.CreateIndex(
                name: "IX_MieleCycles_EndTs",
                table: "MieleCycles",
                column: "EndTs");

            migrationBuilder.CreateIndex(
                name: "IX_MieleStateSnapshots_DeviceId_Ts",
                table: "MieleStateSnapshots",
                columns: new[] { "DeviceId", "Ts" });

            migrationBuilder.CreateIndex(
                name: "IX_Plugs_Mac_Channel",
                table: "Plugs",
                columns: new[] { "Mac", "Channel" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Plugs_SingleMainMeter",
                table: "Plugs",
                column: "Role",
                unique: true,
                filter: "[Role] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_RelayCommands_PlugId_Ts",
                table: "RelayCommands",
                columns: new[] { "PlugId", "Ts" });

            migrationBuilder.CreateIndex(
                name: "idx_samples_plug_time",
                table: "Samples",
                columns: new[] { "PlugId", "TimestampUtc" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApplianceRuns");

            migrationBuilder.DropTable(
                name: "ApplianceSignatures");

            migrationBuilder.DropTable(
                name: "ClimateDaily");

            migrationBuilder.DropTable(
                name: "ClimateReadings");

            migrationBuilder.DropTable(
                name: "ClimateSensors");

            migrationBuilder.DropTable(
                name: "DailyEnergy");

            migrationBuilder.DropTable(
                name: "EntityPrefs");

            migrationBuilder.DropTable(
                name: "GenericDevices");

            migrationBuilder.DropTable(
                name: "GenericEntities");

            migrationBuilder.DropTable(
                name: "GenericHourly");

            migrationBuilder.DropTable(
                name: "GenericReadings");

            migrationBuilder.DropTable(
                name: "HeatingDevices");

            migrationBuilder.DropTable(
                name: "HeatingEnergyDaily");

            migrationBuilder.DropTable(
                name: "HeatingEnergyPeriods");

            migrationBuilder.DropTable(
                name: "HeatingRawFeatures");

            migrationBuilder.DropTable(
                name: "HeatingReadings");

            migrationBuilder.DropTable(
                name: "LiveMinuteAggregates");

            migrationBuilder.DropTable(
                name: "LiveSamples");

            migrationBuilder.DropTable(
                name: "MeterEvents");

            migrationBuilder.DropTable(
                name: "MieleCycles");

            migrationBuilder.DropTable(
                name: "MieleDevices");

            migrationBuilder.DropTable(
                name: "MieleStateSnapshots");

            migrationBuilder.DropTable(
                name: "MonthlyEnergy");

            migrationBuilder.DropTable(
                name: "OneHourAggregates");

            migrationBuilder.DropTable(
                name: "OneMinuteAggregates");

            migrationBuilder.DropTable(
                name: "Plugs");

            migrationBuilder.DropTable(
                name: "RelayCommands");

            migrationBuilder.DropTable(
                name: "Samples");

            migrationBuilder.DropTable(
                name: "Settings");
        }
    }
}
