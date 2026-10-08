using System.Collections.Generic;
using UnityEngine;

namespace Stationeers.RoverCargo;

/// <summary>Temporary diagnostic on a hitched trailer: which of its colliders are touching something, and what.</summary>
public class TowProbe : MonoBehaviour
{
    public readonly Dictionary<string, string> Contacts = new();

    private void OnCollisionStay(Collision c)
    {
        foreach (var p in c.contacts)
        {
            if (p.thisCollider == null) continue;
            string mine = p.thisCollider.GetType().Name + "(" + p.thisCollider.name + ")";
            Contacts[mine] = p.otherCollider ? p.otherCollider.name : "?";
        }
    }

    public string Drain()
    {
        if (Contacts.Count == 0) return "none";
        var s = string.Join(", ", Contacts.Keys);
        Contacts.Clear();
        return s;
    }
}
