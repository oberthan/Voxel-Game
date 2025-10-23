// === FILE: MeshData.cs ===
using System.Collections.Generic;
using UnityEngine;

// Plain data-only container safe to create on background threads
public class MeshData
{
    public List<Vector3> vertices = new List<Vector3>();
    public List<int> triangles = new List<int>();
    public List<Vector2> uvs = new List<Vector2>();
    public List<Vector3> normals = new List<Vector3>();
}