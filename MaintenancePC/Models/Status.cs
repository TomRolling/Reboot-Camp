namespace MaintenancePC.Models;

/// <summary>État affiché par une petite icône colorée à côté d'un élément de liste.</summary>
public enum Status
{
    None,      // rien à signaler (pas d'icône)
    Ok,        // vert
    Warning,   // orange
    Error,     // rouge
    Working,   // en cours (icône qui tourne)
}
