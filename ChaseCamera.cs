using Assets.Scripts;
using Assets.Scripts.Inventory;
using Assets.Scripts.Util;
using Assets.Scripts.Vehicles;
using UnityEngine;

namespace Stationeers.RoverCargo;

/// <summary>
/// Third-person chase camera for any wheeled vehicle (Mk I and Cargo Rover). Vanilla toggles third person with the
/// third-person key + mouse wheel even while seated, but only vehicles implementing IVehicleCamera (the shuttle)
/// get a camera position, so rovers stayed first person. This runs as a postfix of
/// CameraController.CacheCameraPosition and places the camera behind the vehicle; the mouse orbits.
/// </summary>
public static class ChaseCamera
{
    public static bool Enabled = true;
    public static float BaseDistance = 8f;
    public static float Height = 2.2f;

    private static float _yaw, _pitch = 12f;
    private static WheeledBase _vehicle;
    private static readonly RaycastHit[] Hits = new RaycastHit[16];

    public static void AfterCacheCameraPosition(CameraController __instance)
    {
        if (!Enabled || !CameraController.IsThirdPerson) { _vehicle = null; return; }
        var human = InventoryManager.ParentHuman;
        var vehicle = human != null ? human.ParentSlot?.Parent as WheeledBase : null;
        if (vehicle == null || vehicle is CargoHab) { _vehicle = null; return; }   // first person inside the hab
        if (vehicle != _vehicle) { _vehicle = vehicle; _yaw = 0f; _pitch = 12f; }

        if (!Cursor.visible)
        {
            _yaw += Singleton<InputManager>.Instance.GetAxis("LookX") * 2f;
            _pitch = Mathf.Clamp(_pitch - Singleton<InputManager>.Instance.GetAxis("LookY") * 2f, -10f, 70f);
        }

        // Vanilla zoom offset z runs from TPCMinZoom (switch point) down to -TPCMaxZoom; map it to 8..14 m.
        float zoom = Mathf.Clamp(__instance.TPCMinZoom - ThirdPersonOrbitCam.zoomOffset.z, 0f, 6f);
        float distance = BaseDistance + zoom;

        var t = vehicle.transform;
        Vector3 target = t.position + Vector3.up * Height;
        Quaternion rot = Quaternion.Euler(_pitch, t.eulerAngles.y + _yaw, 0f);
        Vector3 desired = target - rot * Vector3.forward * distance;
        desired = Unblocked(vehicle, target, desired);

        var cam = __instance.MainCameraTransform;
        cam.SetPositionAndRotation(desired, Quaternion.LookRotation(target - desired, Vector3.up));
        CameraController.CameraPosition = desired;
        CameraController.CameraRotation = cam.rotation;
        CameraController.EffectiveCameraPosition = desired;
    }

    /// <summary>Pull the camera in front of terrain or structures between the vehicle and the camera.</summary>
    private static Vector3 Unblocked(WheeledBase vehicle, Vector3 from, Vector3 to)
    {
        Vector3 dir = to - from;
        float len = dir.magnitude;
        if (len < 0.01f) return to;
        int n = Physics.RaycastNonAlloc(from, dir / len, Hits, len, ~0, QueryTriggerInteraction.Ignore);
        float best = len;
        for (int i = 0; i < n; i++)
        {
            var hit = Hits[i];
            if (hit.collider == null || hit.collider.transform.IsChildOf(vehicle.transform)) continue;
            if (hit.collider.attachedRigidbody != null && hit.collider.attachedRigidbody.transform.IsChildOf(vehicle.transform)) continue;
            if (hit.collider.GetComponentInParent<Assets.Scripts.Objects.Entities.Human>() != null) continue;
            if (hit.distance < best) best = hit.distance;
        }
        return from + dir / len * Mathf.Max(1.5f, best - 0.3f);
    }
}
