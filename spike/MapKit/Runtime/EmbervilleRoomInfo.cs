// Emberville Map Kit: copy this file into your Unity project at Assets/MapKit/EmbervilleRoomInfo.cs (not in an Editor
// folder). The kit puts it on a room's root (Emberville > Update Room Layers, Create Starter Room): which build of the
// game the room was made on, so opening it on another build warns. Never in the bundle (Build Bundles takes it out).
using UnityEngine;

[DisallowMultipleComponent]
[AddComponentMenu("")]
public class EmbervilleRoomInfo : MonoBehaviour
{
    [Tooltip("The game's Steam build this room was made on")]
    public string gameBuild = "";
}
