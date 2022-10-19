﻿using System;
using System.IO;
using System.Threading;
using AutoDarkModeSvc.Monitors;
using AutoDarkModeLib;
using AutoDarkModeSvc.Handlers.ThemeFiles;
using AutoDarkModeSvc.Core;
using AutoDarkModeLib.Configs;
using static AutoDarkModeSvc.Handlers.IThemeManager.TmHandler;
using AutoDarkModeSvc.Handlers.IThemeManager;

namespace AutoDarkModeSvc.Handlers
{
    public static class ThemeHandler
    {
        private static readonly object _syncRoot = new();

        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

        private static readonly GlobalState state = GlobalState.Instance();
        private static AdmConfigBuilder builder = AdmConfigBuilder.Instance();

        public static bool ThemeModeNeedsUpdate(Theme newTheme, bool skipCheck = false)
        {
            if (builder.Config.WindowsThemeMode.DarkThemePath == null || builder.Config.WindowsThemeMode.LightThemePath == null)
            {
                Logger.Error("dark or light theme path empty");
                return false;
            }
            if (!File.Exists(builder.Config.WindowsThemeMode.DarkThemePath))
            {
                Logger.Error($"invalid dark theme path: {builder.Config.WindowsThemeMode.DarkThemePath}");
                return false;
            }
            if (!File.Exists(builder.Config.WindowsThemeMode.LightThemePath))
            {
                Logger.Error($"invalid light theme path: {builder.Config.WindowsThemeMode.LightThemePath}");
                return false;
            }
            if (!builder.Config.WindowsThemeMode.DarkThemePath.EndsWith(".theme") || !builder.Config.WindowsThemeMode.DarkThemePath.EndsWith(".theme"))
            {
                Logger.Error("both theme paths must have a .theme extension");
                return false;
            }

            // TODO change tracking when having active theme monitor disabled
            if (newTheme == Theme.Dark && (skipCheck ||
                (!state.UnmanagedActiveThemePath.Equals(Helper.UnmanagedDarkThemePath))))
            {
                return true;
            }
            else if (newTheme == Theme.Light && (skipCheck ||
                (!state.UnmanagedActiveThemePath.Equals(Helper.UnmanagedLightThemePath))))
            {
                return true;
            }
            return false;
        }

        /// <summary>
        /// Applies the theme using the KAWAII Theme switcher logic for windows theme files
        /// </summary>
        /// <param name="config"></param>
        /// <param name="newTheme"></param>
        /// <param name="automatic"></param>
        /// <param name="sunset"></param>
        /// <param name="sunrise"></param>
        /// <returns>true if an update was performed; false otherwise</returns>
        public static void ApplyTheme(Theme newTheme)
        {
            PowerHandler.RequestDisableEnergySaver(builder.Config);
            if (builder.Config.WindowsThemeMode.MonitorActiveTheme)
            {
                WindowsThemeMonitor.PauseThemeMonitor(TimeSpan.FromSeconds(10));
            }
            if (newTheme == Theme.Light)
            {
                ThemeFile light = ThemeFile.MakeUnmanagedTheme(builder.Config.WindowsThemeMode.LightThemePath, Helper.UnmanagedLightThemePath);
                light.UnmanagedOriginalName = light.DisplayName;
                light.DisplayName = Helper.UnmanagedLightThemeName;
                ThemeFile.PatchColorsWin11AndSave(light, "0 0 1");
                Apply(builder.Config.WindowsThemeMode.LightThemePath, unmanagedPatched: light);
            }
            else if (newTheme == Theme.Dark)
            {
                ThemeFile dark = ThemeFile.MakeUnmanagedTheme(builder.Config.WindowsThemeMode.DarkThemePath, Helper.UnmanagedDarkThemePath);
                dark.UnmanagedOriginalName = dark.DisplayName;
                dark.DisplayName = Helper.UnmanagedDarkThemeName;
                ThemeFile.PatchColorsWin11AndSave(dark, "0 1 0");
                Apply(builder.Config.WindowsThemeMode.DarkThemePath, unmanagedPatched: dark);
            }
        }

        public static void ApplyManagedTheme(AdmConfig config, string path)
        {
            Apply(path);
        }

        public static string GetCurrentThemeName()
        {
            string themeName = "";/*Exception applyEx = null;*/
            Thread thread = new(() =>
            {
                try
                {
                    themeName = new ThemeManagerClass().CurrentTheme.DisplayName;
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, $"could not retrieve active theme name");
                    //applyEx = ex;
                }
            })
            {
                Name = "COMThemeManagerThread"
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            try
            {
                thread.Join();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "theme handler thread was interrupted");
            }
            return themeName;
        }

        private static void ApplyIThemeManager(string originalPath, bool suppressLogging = false, ThemeFile unmanaged = null)
        {

            string themeFilePath = unmanaged != null ? unmanaged.ThemeFilePath : originalPath;

            /*Exception applyEx = null;*/
            Thread thread = new(() =>
            {
                try
                {
                    new ThemeManagerClass().ApplyTheme(themeFilePath);
                    state.UnmanagedActiveThemePath = themeFilePath;
                    if (!suppressLogging)
                    {
                        if (unmanaged != null) Logger.Info($"applied theme \"{originalPath}\" via IThemeManager");
                        else Logger.Info($"applied theme \"{themeFilePath}\" via IThemeManager");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, $"could not apply theme \"{themeFilePath}\"");
                }
            })
            {
                Name = "COMThemeManagerThread"
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            try
            {
                thread.Join();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "theme handler thread was interrupted");
            }
        }

        private static void ApplyIThemeManager2(string originalPath, bool suppressLogging = false, ThemeFile unmanagedPatched = null)
        {
            DateTime start = DateTime.Now;

            string themeFilePath = unmanagedPatched != null ? unmanagedPatched.ThemeFilePath : originalPath;

            bool tm2Found = false;
            bool tm2Success = false;
            string displayNameFromFile = null;
            try
            {
                (_, displayNameFromFile) = ThemeFile.GetDisplayNameFromRaw(themeFilePath);
                (tm2Found, tm2Success) = IThemeManager2.Tm2Handler.SetTheme(displayNameFromFile, originalPath);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"could not retrieve display name for path {themeFilePath}:");
            }

            if (!tm2Found && tm2Success)
            {
                Logger.Warn("theme name not found in IThemeManager2, using mitigation");
                ApplyIThemeManager(themeFilePath, suppressLogging);
                string displayNameApi = GetCurrentThemeName();
                if (!state.LearnedThemeNames.ContainsKey(displayNameFromFile))
                {
                    Logger.Debug($"learned new theme name association: {displayNameFromFile}={displayNameApi}");
                    state.LearnedThemeNames.Add(displayNameFromFile, displayNameApi);
                }
                else
                {
                    Logger.Debug($"updated theme name association: {displayNameFromFile}={displayNameApi}");
                    state.LearnedThemeNames[displayNameFromFile] = displayNameApi;
                }
            }

            state.UnmanagedActiveThemePath = themeFilePath;

            DateTime end = DateTime.Now;
            TimeSpan elapsed = end - start;

            if (elapsed.TotalSeconds > 10 && tm2Success)
            {
                Logger.Warn($"theme switching took longer than expected ({elapsed.TotalSeconds} seconds)");
            }
        }

        public static void Apply(string themeFilePath, bool suppressLogging = false, ThemeFile unmanagedPatched = null)
        {
            if (Environment.OSVersion.Version.Build >= (int)WindowsBuilds.MinBuildForNewFeatures)
            {
                ApplyIThemeManager2(themeFilePath, suppressLogging, unmanagedPatched);
            }
            else
            {
                ApplyIThemeManager(themeFilePath, suppressLogging, unmanagedPatched);
            }
        }

        public static string GetCurrentVisualStyleName()
        {
            return Path.GetFileName(new ThemeManagerClass().CurrentTheme.VisualStyle);
        }

        /// <summary>
        /// Forces the theme to update when the automatic theme detection is disabled
        /// </summary>
        /// <param name="builder"></param>
        /// <param name="state"></param>
        /// <param name="theme"></param>
        public static void EnforceNoMonitorUpdates(AdmConfigBuilder builder, GlobalState state, Theme theme)
        {
            string themePath = "";
            switch (theme)
            {
                case Theme.Light:
                    themePath = Helper.UnmanagedLightThemePath;
                    break;
                case Theme.Dark:
                    themePath = Helper.UnmanagedDarkThemePath;
                    break;
            }
            if (builder.Config.WindowsThemeMode.Enabled
                && !builder.Config.WindowsThemeMode.MonitorActiveTheme
                && state.UnmanagedActiveThemePath == themePath)
            {
                Logger.Debug("enforcing theme refresh with disabled MonitorActiveTheme");
                state.UnmanagedActiveThemePath = "";
            }
        }
    }
}