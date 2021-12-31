﻿using System;
using System.Collections.Generic;
using System.Linq;
using Playnite.SDK;
using Playnite.SDK.Plugins;

namespace DLSiteMetadata;

public class Settings : ISettings
{
    private readonly IPlayniteAPI? _playniteAPI;
    private readonly Plugin? _plugin;

    public string? PreferredLanguage { get; set; } = Scrapper.DefaultLanguage;

    public bool IncludeScenarioWriters { get; set; } = true;
    public bool IncludeIllustrators { get; set; } = true;
    public bool IncludeVoiceActors { get; set; } = true;
    public bool IncludeMusicCreators { get; set; } = true;

    public List<string> AvailableLanguages { get; } = new()
    {
        "en_US",
        "ja_JP",
        "ko_KR",
        "zh_CN",
        "zh_TW",
    };

    public Settings() { }

    public Settings(IPlayniteAPI playniteAPI, Plugin plugin)
    {
        _playniteAPI = playniteAPI;
        _plugin = plugin;

        var savedSettings = plugin.LoadPluginSettings<Settings>();
        if (savedSettings is not null)
        {
            PreferredLanguage = savedSettings.PreferredLanguage;
        }
    }

    private Settings? _previousSettings;

    public void BeginEdit()
    {
        _previousSettings = new Settings
        {
            PreferredLanguage = PreferredLanguage,
            IncludeIllustrators = IncludeIllustrators,
            IncludeMusicCreators = IncludeMusicCreators,
            IncludeScenarioWriters = IncludeScenarioWriters,
            IncludeVoiceActors = IncludeVoiceActors
        };
    }

    public void EndEdit()
    {
        _previousSettings = null;
        _plugin?.SavePluginSettings(this);
    }

    public void CancelEdit()
    {
        if (_previousSettings is null) return;

        PreferredLanguage = _previousSettings.PreferredLanguage;
        IncludeIllustrators = _previousSettings.IncludeIllustrators;
        IncludeMusicCreators = _previousSettings.IncludeMusicCreators;
        IncludeScenarioWriters = _previousSettings.IncludeScenarioWriters;
        IncludeVoiceActors = _previousSettings.IncludeVoiceActors;
    }

    public bool VerifySettings(out List<string> errors)
    {
        errors = new List<string>();

        if (PreferredLanguage is null)
        {
            errors.Add("You must select a preferred language!");
        }
        else if (!AvailableLanguages.Any(x => x.Equals(PreferredLanguage, StringComparison.OrdinalIgnoreCase)))
        {
            errors.Add($"Unknown language: \"{PreferredLanguage}\"");
        }

        return !errors.Any();
    }
}
