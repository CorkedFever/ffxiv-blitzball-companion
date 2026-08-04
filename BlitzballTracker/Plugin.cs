using Dalamud.Game.Chat;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace BlitzballTracker;

using BlitzballTracker.Core.GameState;
using BlitzballTracker.Core.Parsing;
using BlitzballTracker.UI;
using BlitzballTracker.UI.Views;
using BlitzballTracker.Windows;

public sealed class Plugin : IDalamudPlugin
{
    public string Name => "Blitzball Companion";

    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly IChatGui _chatGui;
    private readonly IPluginLog _log;
    private readonly ICommandManager _commandManager;
    private readonly IObjectTable _objectTable;
    private readonly IClientState _clientState;
    private readonly IFramework _framework;

    private readonly Configuration _config;
    private readonly BlitzGame _gameState;
    private readonly ChatParser _parser;
    private readonly GameRecorder _recorder;
    private readonly LiveFeedClient _liveFeed;

    private readonly WaymarkReader _waymarks;
    private readonly DemoDirector _demo;
    private readonly MatchDriver _driver;
    private readonly WorldOverlay _worldOverlay;

    private readonly WindowSystem _windowSystem;
    private readonly ShellWindow _shell;

    /// <summary>Positions are polled rather than read every frame; the field does not move that fast.</summary>
    private static readonly TimeSpan PositionSyncInterval = TimeSpan.FromMilliseconds(250);
    private DateTime _lastPositionSync = DateTime.MinValue;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        IChatGui chatGui,
        IPluginLog log,
        ICommandManager commandManager,
        IObjectTable objectTable,
        IClientState clientState,
        IFramework framework,
        IGameGui gameGui)
    {
        _pluginInterface = pluginInterface;
        _chatGui = chatGui;
        _log = log;
        _commandManager = commandManager;
        _objectTable = objectTable;
        _clientState = clientState;
        _framework = framework;

        _config = _pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        _gameState = new BlitzGame();
        _gameState.Rules.StandbyStatus = _config.StandbyStatus;
        _parser = new ChatParser(_gameState);
        _recorder = new GameRecorder();
        _liveFeed = new LiveFeedClient(log);

        _waymarks = new WaymarkReader(_objectTable, _log);
        _demo = new DemoDirector(_objectTable, _gameState);
        _waymarks.Demo = _demo;

        var recordings = Path.Combine(_pluginInterface.GetPluginConfigDirectory(), "recordings");
        _driver = new MatchDriver(_gameState, _parser, _demo, _log, recordings);
        _driver.RefreshRecordings();

        _worldOverlay = new WorldOverlay(_gameState, _waymarks, gameGui, _config);

        // Restore the roster from the last session, so a reload mid-match does not
        // mean re-entering twelve names.
        if (_config.LastRoster is { NamedCount: > 0 } saved)
        {
            _gameState.ApplyRoster(saved);
            _log.Info($"Restored roster: {saved.HomeTeam} vs {saved.AwayTeam} ({saved.Entries.Count} players).");
        }

        IShellView[] views =
        [
            new MatchView(_gameState, _recorder, _liveFeed, _driver),
            new RosterView(_gameState, _config, _pluginInterface, _waymarks, _parser, _liveFeed),
            new StatsView(_gameState),
            new LabView(_driver, _gameState, _waymarks, _config),
            new SettingsView(_gameState, _config, _pluginInterface, _recorder, _liveFeed, recordings),
        ];

        _shell = new ShellWindow(views) { IsOpen = true };

        _windowSystem = new WindowSystem("BlitzballTracker");
        _windowSystem.AddWindow(_shell);

        _chatGui.ChatMessage += OnChatMessage;
        _framework.Update += OnFrameworkUpdate;

        _pluginInterface.UiBuilder.Draw += _windowSystem.Draw;
        _pluginInterface.UiBuilder.Draw += DrawWorldOverlay;
        _pluginInterface.UiBuilder.OpenConfigUi += OnOpenConfigUi;
        _pluginInterface.UiBuilder.OpenMainUi += OnOpenMainUi;

        _commandManager.AddHandler("/blitz", new Dalamud.Game.Command.CommandInfo(OnCommand)
        {
            HelpMessage =
                "Open the Blitzball Companion.\n" +
                "Everything is in the window; these are just shortcuts.\n" +
                "/blitz match | roster | stats | lab | settings — jump to a screen",
        });

        // Without a roster nothing can be tracked, so start where the work is.
        if (!_gameState.HasRoster)
            _shell.Navigate("Roster");
    }

    private void OnChatMessage(IHandleableChatMessage chat)
    {
        // We care about Yell (0x1E), Dice Roll (0x4A), and Field Marker (0x49/0xC9)
        var typeCode = (ushort)chat.LogKind & 0xFF;
        if (typeCode is not (0x1E or 0x4A or 0x49 or 0xC9))
            return;

        var senderText = chat.Sender.TextValue;
        var messageText = chat.Message.TextValue;

        _recorder.Write(chat.LogKind, senderText, messageText);
        _liveFeed.Send(chat.LogKind, senderText, messageText);

        try
        {
            _parser.ProcessMessage(senderText, messageText, DateTime.Now);
        }
        catch (Exception ex)
        {
            _log.Error($"[BlitzTracker] Parse error: {ex.Message}");
        }
    }

    /// <summary>
    /// Advance playback, then keep tracked positions in step with where players
    /// actually are.
    ///
    /// The game wins over chat here: a declared [MOVE to X] that does not match the
    /// player's real position is reported rather than believed.
    /// </summary>
    private void OnFrameworkUpdate(IFramework framework)
    {
        _driver.Step();

        var now = DateTime.Now;
        if (now - _lastPositionSync < PositionSyncInterval) return;
        _lastPositionSync = now;

        if (!_clientState.IsLoggedIn) return;

        // "You roll a ..." lines are attributed to whoever is logged in.
        if (string.IsNullOrEmpty(_parser.LocalPlayerName))
            _parser.LocalPlayerName = _objectTable.LocalPlayer?.Name.TextValue;

        // Stand-in bodies are derived from tracked state, so they follow playback.
        // They are placed on whichever arena is actually in use: the real waymarks
        // when a venue has them down, the fabricated ones otherwise.
        if (_demo.Enabled)
            _demo.Refresh(_waymarks.ReadMarkers());

        if (!_gameState.IsActive || !_gameState.HasRoster) return;

        try
        {
            _waymarks.SyncPositions(_gameState, (player, declared, actual) =>
            {
                if (declared == Waymark.None) return; // first placement, nothing to contradict

                _gameState.PlayByPlay.Add($"[{now:HH:mm:ss}] ⚑ {player} is at {actual}, not {declared}.");
            });
        }
        catch (Exception ex)
        {
            _log.Error($"[BlitzTracker] Position sync failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Draw the in-arena overlay. Failures disable it rather than throwing every
    /// frame, since a draw handler that keeps faulting would flood the log.
    /// </summary>
    private void DrawWorldOverlay()
    {
        try
        {
            _worldOverlay.Draw();
        }
        catch (Exception ex)
        {
            _config.ShowWorldOverlay = false;
            _log.Error($"[BlitzTracker] Arena overlay disabled after an error: {ex}");
            _chatGui.Print("[BlitzTracker] Arena overlay hit an error and was turned off. See /xllog.");
        }
    }

    /// <summary>
    /// Shortcuts only. Everything these do is reachable by clicking, so the command
    /// exists for speed rather than as the interface.
    /// </summary>
    private void OnCommand(string command, string args)
    {
        var target = args.Trim();

        if (target.Length == 0)
        {
            _shell.IsOpen = !_shell.IsOpen;
            return;
        }

        _shell.Navigate(target);
    }

    private void OnOpenMainUi() => _shell.IsOpen = true;

    private void OnOpenConfigUi() => _shell.Navigate("Settings");

    public void Dispose()
    {
        _recorder.Dispose();
        _liveFeed.Dispose();

        _framework.Update -= OnFrameworkUpdate;
        _chatGui.ChatMessage -= OnChatMessage;

        _pluginInterface.UiBuilder.Draw -= _windowSystem.Draw;
        _pluginInterface.UiBuilder.Draw -= DrawWorldOverlay;
        _pluginInterface.UiBuilder.OpenConfigUi -= OnOpenConfigUi;
        _pluginInterface.UiBuilder.OpenMainUi -= OnOpenMainUi;

        _commandManager.RemoveHandler("/blitz");
        _windowSystem.RemoveAllWindows();
    }
}
