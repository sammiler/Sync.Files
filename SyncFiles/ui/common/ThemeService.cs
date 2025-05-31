// In SyncFiles/UI/Common/ThemeService.cs
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace SyncFiles.UI.Common
{
    public class ThemeService : INotifyPropertyChanged // Ensure class is public
    {
        // Keep static instance for code access if needed, XAML will create its own via Resources
        private static readonly Lazy<ThemeService> _lazyInstance = new Lazy<ThemeService>(() => new ThemeService());
        public static ThemeService Instance => _lazyInstance.Value;

        private bool _isDarkTheme;
        public bool IsDarkTheme
        {
            get => _isDarkTheme;
            private set // Setter should be private or internal if only updated by this class
            {
                if (_isDarkTheme != value)
                {
                    _isDarkTheme = value;
                    OnPropertyChanged();
                }
            }
        }

        // **** Make constructor public and parameterless for XAML ****
        public ThemeService()
        {
            // It's important that this constructor doesn't do too much work
            // or rely on things not available at design time if used heavily by designer.
            // However, for runtime, it's fine.
            UpdateThemeInternal();
            VSColorTheme.ThemeChanged += OnVsThemeChanged;
        }

        private void OnVsThemeChanged(ThemeChangedEventArgs e)
        {
            Application.Current?.Dispatcher?.Invoke(UpdateThemeInternal);
        }

        public void UpdateTheme()
        {
            UpdateThemeInternal();
        }

        private void UpdateThemeInternal()
        {
            // Check if we are in design mode or if Application.Current is not available
            if (DesignerProperties.GetIsInDesignMode(new DependencyObject()) || Application.Current == null)
            {
                IsDarkTheme = false; // Default for design time or no app context
                return;
            }

            // This check is critical and should ideally be on UI thread
            // If UpdateThemeInternal can be called from a background thread, this needs marshalling.
            // Assuming VSColorTheme.ThemeChanged and initial call from constructor are on appropriate threads or handled.
            if (!ThreadHelper.CheckAccess())
            {
                System.Diagnostics.Debug.WriteLine("ThemeService.UpdateThemeInternal called from non-UI thread. Marshalling.");
                Application.Current.Dispatcher.Invoke(UpdateThemeInternal); // Recursive call on UI thread
                return;
            }

            try
            {
                var backgroundColor = VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowBackgroundColorKey);
                IsDarkTheme = (backgroundColor.R * 0.299 + backgroundColor.G * 0.587 + backgroundColor.B * 0.114) < 128;
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in UpdateThemeInternal: {ex.Message}");
                IsDarkTheme = false;
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public void UnsubscribeThemeChangedEvent() // Instance method now
        {
            VSColorTheme.ThemeChanged -= OnVsThemeChanged;
        }
    }
}