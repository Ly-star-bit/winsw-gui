using System;
using Microsoft.Win32;

namespace WinSW.Gui.Services
{
    /// <summary>
    /// Thin wrappers over the common file dialogs, so view models stay free of dialog code.
    /// </summary>
    public static class Dialogs
    {
        public static string? PickFile(string title, string filter, string? initialDirectory = null)
        {
            var dialog = new OpenFileDialog
            {
                Title = title,
                Filter = filter,
                CheckFileExists = true,
                Multiselect = false,
            };

            if (!string.IsNullOrEmpty(initialDirectory))
            {
                dialog.InitialDirectory = initialDirectory;
            }

            return dialog.ShowDialog() == true ? dialog.FileName : null;
        }

        public static string? PickSaveFile(string title, string filter, string suggestedName)
        {
            var dialog = new SaveFileDialog
            {
                Title = title,
                Filter = filter,
                FileName = suggestedName,
                OverwritePrompt = true,
                AddExtension = true,
                DefaultExt = ".xml",
            };

            return dialog.ShowDialog() == true ? dialog.FileName : null;
        }

        public static string? PickFolder(string description, string? initialDirectory = null)
        {
            var dialog = new OpenFolderDialog
            {
                Title = description,
                Multiselect = false,
            };

            if (!string.IsNullOrEmpty(initialDirectory))
            {
                dialog.InitialDirectory = initialDirectory;
            }

            return dialog.ShowDialog() == true ? dialog.FolderName : null;
        }
    }
}
