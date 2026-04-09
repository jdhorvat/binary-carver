using System.Windows;
using System.Windows.Controls;
using BinaryCarver.Models;

namespace BinaryCarver;

public partial class SignatureEditorWindow : Window
{
    public List<CustomSignature> Signatures { get; private set; }

    public SignatureEditorWindow(List<CustomSignature> existing)
    {
        InitializeComponent();
        // Deep copy so cancel doesn't mutate the original list
        Signatures = existing.Select(s => new CustomSignature
        {
            FileType     = s.FileType,
            Description  = s.Description,
            MagicHex     = s.MagicHex,
            SearchOffset = s.SearchOffset,
            IsTextBased  = s.IsTextBased,
            TextPrefix   = s.TextPrefix,
            DefaultExt   = s.DefaultExt,
        }).ToList();

        RefreshGrid();
    }

    private void RefreshGrid()
    {
        GridSigs.ItemsSource = null;
        GridSigs.ItemsSource = Signatures;
    }

    private void GridSigs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GridSigs.SelectedItem is CustomSignature sig)
        {
            TxtSigType.Text    = sig.FileType;
            TxtSigDesc.Text    = sig.Description;
            TxtSigMagic.Text   = sig.MagicHex;
            TxtSigOffset.Text  = sig.SearchOffset.ToString();
            ChkTextBased.IsChecked = sig.IsTextBased;
            TxtSigExt.Text     = sig.DefaultExt;
        }
    }

    private void BtnAdd_Click(object sender, RoutedEventArgs e)
    {
        string fileType = TxtSigType.Text.Trim();
        if (string.IsNullOrWhiteSpace(fileType))
        {
            MessageBox.Show("File Type is required.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        int.TryParse(TxtSigOffset.Text.Trim(), out int searchOffset);

        var sig = new CustomSignature
        {
            FileType     = fileType,
            Description  = TxtSigDesc.Text.Trim(),
            MagicHex     = TxtSigMagic.Text.Trim().Replace(" ", ""),
            SearchOffset = searchOffset,
            IsTextBased  = ChkTextBased.IsChecked == true,
            TextPrefix   = ChkTextBased.IsChecked == true ? TxtSigMagic.Text.Trim() : "",
            DefaultExt   = TxtSigExt.Text.Trim(),
        };

        if (!sig.IsValid)
        {
            MessageBox.Show("Signature needs at least a File Type and either Magic hex bytes or a Text Prefix.",
                "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Signatures.Add(sig);
        RefreshGrid();
        ClearForm();
    }

    private void BtnRemove_Click(object sender, RoutedEventArgs e)
    {
        if (GridSigs.SelectedItem is CustomSignature sig)
        {
            Signatures.Remove(sig);
            RefreshGrid();
            ClearForm();
        }
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void ClearForm()
    {
        TxtSigType.Text   = "";
        TxtSigDesc.Text   = "";
        TxtSigMagic.Text  = "";
        TxtSigOffset.Text = "0";
        TxtSigExt.Text    = ".bin";
        ChkTextBased.IsChecked = false;
    }
}
