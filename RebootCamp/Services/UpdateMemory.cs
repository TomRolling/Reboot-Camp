using System.IO;
using System.Text.Json;

namespace RebootCamp.Services;

/// <summary>Ce que l'appli a retenu d'une tentative de mise à jour d'un logiciel.</summary>
/// <param name="Version">La version que winget proposait à ce moment-là.</param>
/// <param name="Blocked">Vrai si winget n'a pas pu faire cette mise à jour (faux : elle a réussi).</param>
/// <param name="Reason">L'explication affichée, si la mise à jour est bloquée.</param>
/// <param name="CanReinstall">Vrai si « désinstaller puis réinstaller » peut débloquer la situation.</param>
public sealed record UpdateNote(string Version, bool Blocked, string Reason = "", bool CanReinstall = false);

/// <summary>
/// Petit carnet de l'onglet Logiciels, gardé d'une ouverture de l'appli à l'autre dans un fichier du dossier de
/// l'appli (%LOCALAPPDATA%\RebootCamp\logiciels.json). Il sert à deux choses :
/// - ne plus proposer en boucle une mise à jour déjà faite, quand winget lit mal le numéro de version du logiciel ;
/// - mettre à part les mises à jour que winget ne peut pas faire sur ce PC, au lieu de les relancer pour rien.
/// Une note ne vaut que pour la version proposée à l'époque : dès que winget en propose une autre, on réessaie.
/// </summary>
public sealed class UpdateMemory
{
    private readonly string _file;
    private readonly Dictionary<string, UpdateNote> _notes;

    public UpdateMemory(string? file = null)
    {
        _file = file ?? Path.Combine(AppInfo.LocalFolder, "logiciels.json");
        _notes = Load(_file);
    }

    /// <summary>La note laissée pour ce logiciel, si elle concerne bien la version proposée aujourd'hui.</summary>
    public UpdateNote? Find(string id, string availableVersion) =>
        _notes.TryGetValue(id, out var note) && note.Version == availableVersion ? note : null;

    public void Remember(string id, UpdateNote note)
    {
        _notes[id] = note;
        Save();
    }

    /// <summary>Oublie les mises à jour réussies : les logiciels masqués seront de nouveau proposés.</summary>
    public void ForgetDone()
    {
        foreach (string id in _notes.Where(n => !n.Value.Blocked).Select(n => n.Key).ToList()) _notes.Remove(id);
        Save();
    }

    public void Forget(string id)
    {
        if (_notes.Remove(id)) Save();
    }

    private static Dictionary<string, UpdateNote> Load(string file)
    {
        try
        {
            if (File.Exists(file)
                && JsonSerializer.Deserialize<Dictionary<string, UpdateNote>>(File.ReadAllText(file)) is { } notes)
                return new Dictionary<string, UpdateNote>(notes, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Fichier illisible ou abîmé : on repart d'un carnet vide, sans gravité.
        }
        return new Dictionary<string, UpdateNote>(StringComparer.OrdinalIgnoreCase);
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, JsonSerializer.Serialize(_notes, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Error("Enregistrement du carnet des logiciels", ex);
        }
    }
}
