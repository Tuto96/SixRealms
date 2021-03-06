﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UI;

public class HexGrid : MonoBehaviour
{

    [Header ("Prefabs")]
    public HexCell cellPrefab;
    public Text cellLabelPrefab;
    public HexGridChunk chunkPrefab;

    [Header ("Map Generation")]
    public string seed;
    public int cellCountX = 20, cellCountZ = 15;
    public Texture2D noiseSource;
    int chunkCountX, chunkCountZ;
    int localHour = 6;
    HexCell[] cells;
    HexGridChunk[] chunks;
    HexCellPriorityQueue searchFrontier;
    HexCell currentPathFrom, currentPathTo;
    bool currentPathExists;
    int searchFrontierPhase;
    HexCellShaderData cellShaderData;

    #region Initialization and Settings

    public void Init (int sizeX, int sizeZ)
    {
        cellCountX = sizeX;
        cellCountZ = sizeZ;
        HexMetrics.noiseSource = noiseSource;
        HexMetrics.InitializeHashGrid (seed);
        cellShaderData = gameObject.AddComponent<HexCellShaderData> ();
        cellShaderData.Grid = this;
        CreateMap (cellCountX, cellCountZ);
    }
    void OnEnable ()
    {
        if (!HexMetrics.noiseSource && GameManager.instance.inGame)
        {
            HexMetrics.noiseSource = noiseSource;
            HexMetrics.InitializeHashGrid (seed);
            ResetVisibility ();
        }
    }

    #endregion

    #region Hex Grid Map

    public bool CreateMap (int x, int z)
    {
        if (
            x <= 0 || x % HexMetrics.chunkSizeX != 0 ||
            z <= 0 || z % HexMetrics.chunkSizeZ != 0
        )
        {
            Debug.LogError ("Unsupported map size.");
            return false;
        }

        cellCountX = x;
        cellCountZ = z;

        ClearPath ();

        if (chunks != null)
        {
            for (int i = 0; i < chunks.Length; i++)
            {
                Destroy (chunks[i].gameObject);
            }
        }

        chunkCountX = cellCountX / HexMetrics.chunkSizeX;
        chunkCountZ = cellCountZ / HexMetrics.chunkSizeZ;

        cellShaderData.Initialize (cellCountX, cellCountZ);

        CreateChunks ();
        CreateCells ();

        return true;
    }

    void CreateChunks ()
    {
        chunks = new HexGridChunk[chunkCountX * chunkCountZ];

        for (int z = 0, i = 0; z < chunkCountZ; z++)
        {
            for (int x = 0; x < chunkCountX; x++)
            {
                HexGridChunk chunk = chunks[i++] = Instantiate (chunkPrefab);
                chunk.transform.SetParent (transform);
            }
        }
    }

    private void CreateCells ()
    {
        cells = new HexCell[cellCountZ * cellCountX];

        for (int z = 0, i = 0; z < cellCountZ; z++)
        {
            for (int x = 0; x < cellCountX; x++)
            {
                CreateCell (x, z, i++);
            }
        }
    }

    void CreateCell (int x, int z, int i)
    {

        Vector3 position;
        position.x = (x + z * 0.5f - z / 2) * (HexMetrics.innerRadius * 2f);
        position.y = 0f;
        position.z = z * (HexMetrics.outerRadius * 1.5f);

        HexCell cell = cells[i] = Instantiate<HexCell> (cellPrefab);
        //cell.transform.SetParent(transform, false);
        cell.transform.localPosition = position;
        cell.coordinates = HexCoordinates.FromOffsetCoordinates (x, z);
        cell.Index = i;
        cell.ShaderData = cellShaderData;
        cell.Explorable =
            x > 0 && z > 0 && x < cellCountX - 1 && z < cellCountZ - 1;

        if (x > 0)
        {
            cell.SetNeighbor (HexDirection.W, cells[i - 1]);
        }
        if (z > 0)
        {
            if ((z & 1) == 0)
            {
                cell.SetNeighbor (HexDirection.SE, cells[i - cellCountX]);
                if (x > 0)
                {
                    cell.SetNeighbor (HexDirection.SW, cells[i - cellCountX - 1]);
                }
            }
            else
            {
                cell.SetNeighbor (HexDirection.SW, cells[i - cellCountX]);
                if (x < cellCountX - 1)
                {
                    cell.SetNeighbor (HexDirection.SE, cells[i - cellCountX + 1]);
                }
            }

        }

        Text label = Instantiate<Text> (cellLabelPrefab);
        //label.rectTransform.SetParent(gridCanvas.transform, false);
        label.rectTransform.anchoredPosition = new Vector2 (position.x, position.z);

        cell.uiRect = label.rectTransform;
        cell.Elevation = 0;
        AddCellToChunk (x, z, cell);
    }

    void AddCellToChunk (int x, int z, HexCell cell)
    {
        int chunkX = x / HexMetrics.chunkSizeX;
        int chunkZ = z / HexMetrics.chunkSizeZ;
        HexGridChunk chunk = chunks[chunkX + chunkZ * chunkCountX];

        int localX = x - chunkX * HexMetrics.chunkSizeX;
        int localZ = z - chunkZ * HexMetrics.chunkSizeZ;
        chunk.AddCell (localX + localZ * HexMetrics.chunkSizeX, cell);
    }

    public HexCell GetCell (Vector3 position)
    {
        position = transform.InverseTransformPoint (position);
        HexCoordinates coordinates = HexCoordinates.FromPosition (position);
        int index = coordinates.X + coordinates.Z * cellCountX + coordinates.Z / 2;
        return cells[index];
    }

    public HexCell GetCell (HexCoordinates coordinates)
    {
        int z = coordinates.Z;
        if (z < 0 || z >= cellCountZ)
        {
            return null;
        }
        int x = coordinates.X + z / 2;
        if (x < 0 || x >= cellCountX)
        {
            return null;
        }
        return cells[x + z * cellCountX];
    }

    #endregion

    #region Pathfinding

    public void FindPath (HexCell fromCell, HexCell toCell, HexUnit unit)
    {
        if (currentPathTo)
        {
            currentPathTo.DisableHighlight ();
        }
        if (!unit.hasMoved)
        {
            ClearPath ();
            currentPathFrom = fromCell;
            currentPathTo = toCell;
            currentPathExists = Search (fromCell, toCell, unit);
            ShowPath (unit);
        }
    }

    void ShowPath (HexUnit unit)
    {
        if (unit.currentState == HexUnitState.Idle)
        {
            if (currentPathExists)
            {
                HexCell current = currentPathTo;
                while (current != currentPathFrom)
                {
                    current.SetLabel ((unit.ActionPoints - current.Distance).ToString ());
                    current.EnableHighlight (GameManager.instance.pathColor);
                    current = current.PathFrom;
                }
                currentPathTo.EnableHighlight (GameManager.instance.validColor);
            }
            else
            {
                currentPathTo.EnableHighlight (GameManager.instance.nonValidColor);
            }
        }
    }

    public bool HasPath
    {
        get
        {
            return currentPathExists;
        }
    }

    public List<HexCell> GetPath ()
    {
        if (!currentPathExists)
        {
            return null;
        }
        List<HexCell> path = ListPool<HexCell>.Get ();
        for (HexCell c = currentPathTo; c != currentPathFrom; c = c.PathFrom)
        {
            path.Add (c);
        }
        path.Add (currentPathFrom);
        path.Reverse ();
        return path;
    }

    public void ClearPath ()
    {
        if (currentPathExists)
        {
            HexCell current = currentPathTo;
            while (current != currentPathFrom)
            {
                current.SetLabel (null);
                current.DisableHighlight ();
                current = current.PathFrom;
            }
            currentPathExists = false;
        }
        currentPathFrom = currentPathTo = null;
    }

    public void ClearFullPath ()
    {

        if (currentPathExists)
        {
            currentPathFrom.DisableHighlight ();
            HexCell current = currentPathTo;
            while (current != currentPathFrom)
            {
                current.SetLabel (null);
                current.DisableHighlight ();
                current = current.PathFrom;
            }
            currentPathExists = false;
        }
        currentPathFrom = currentPathTo = null;
    }

    bool Search (HexCell fromCell, HexCell toCell, HexUnit unit)
    {
        int speed = unit.ActionPoints;
        searchFrontierPhase += 2;

        if (searchFrontier == null)
        {
            searchFrontier = new HexCellPriorityQueue ();
        }
        else
        {
            searchFrontier.Clear ();
        }

        fromCell.SearchPhase = searchFrontierPhase;
        fromCell.Distance = 0;
        searchFrontier.Enqueue (fromCell);
        while (searchFrontier.Count > 0)
        {
            HexCell current = searchFrontier.Dequeue ();
            current.SearchPhase += 1;

            if (current == toCell)
            {
                return true;
            }

            int currentTurn = (current.Distance - 1) / speed;

            for (HexDirection d = HexDirection.NE; d <= HexDirection.NW; d++)
            {
                HexCell neighbor = current.GetNeighbor (d);
                if (neighbor == null || neighbor.SearchPhase > searchFrontierPhase)
                {
                    continue;
                }
                if (!unit.IsValidDestination (neighbor))
                {
                    continue;
                }

                int moveCost = unit.GetMoveCost (current, neighbor, d);
                if (moveCost < 0)
                {
                    continue;
                }

                int distance = current.Distance + moveCost;
                int turn = (distance - 1) / speed;

                if (turn > currentTurn)
                {
                    //distance = turn * speed + moveCost;
                    continue;
                }

                if (neighbor.SearchPhase < searchFrontierPhase)
                {
                    neighbor.SearchPhase = searchFrontierPhase;
                    neighbor.Distance = distance;
                    neighbor.PathFrom = current;
                    neighbor.SearchHeuristic = neighbor.coordinates.DistanceTo (toCell.coordinates);
                    searchFrontier.Enqueue (neighbor);
                }
                else if (distance < neighbor.Distance)
                {
                    int oldPriority = neighbor.SearchPriority;
                    neighbor.Distance = distance;
                    neighbor.PathFrom = current;
                    searchFrontier.Change (neighbor, oldPriority);
                }
            }
        }
        return false;
    }

    #endregion

    #region Visibility

    #endregion
    public List<HexCell> GetVisibleCells (HexCell fromCell, HexUnit unit)
    {
        return GetCellsInRange (fromCell, unit.visionAngle, unit.Direction, unit.visionRange, true, true);
    }
    public List<HexCell> GetCellsInRange (HexCell fromCell, HexAngleType angleType, HexDirection unitDirection, int range, bool elevationAddsRange = false, bool canSeeTruWalls = false)
    {
        List<HexCell> visibleCells = ListPool<HexCell>.Get ();

        searchFrontierPhase += 2;
        if (searchFrontier == null)
        {
            searchFrontier = new HexCellPriorityQueue ();
        }
        else
        {
            searchFrontier.Clear ();
        }

        range += elevationAddsRange ? fromCell.ViewElevation : 0;

        fromCell.SearchPhase = searchFrontierPhase;
        fromCell.Distance = 0;
        searchFrontier.Enqueue (fromCell);
        HexCoordinates fromCoordinates = fromCell.coordinates;
        while (searchFrontier.Count > 0)
        {
            HexCell current = searchFrontier.Dequeue ();
            current.SearchPhase += 1;
            visibleCells.Add (current);
            HexDirection start;
            HexDirection end;
            if (angleType == HexAngleType.Narrow)
            {
                start = unitDirection.Opposite ();
                end = unitDirection.Opposite ().Next ();
                if (canSeeTruWalls)
                {
                    fromCoordinates = LookLimitedTruWalls (range, fromCoordinates, current, start, end);
                }
                else
                {
                    fromCoordinates = LookLimited (range, fromCoordinates, current, start, end);
                }
            }
            else if (angleType == HexAngleType.Regular)
            {
                start = unitDirection.Opposite ().Previous ();
                end = unitDirection.Opposite ().Next2 ();
                if (canSeeTruWalls)
                {
                    fromCoordinates = LookLimitedTruWalls (range, fromCoordinates, current, start, end);
                }
                else
                {
                    fromCoordinates = LookLimited (range, fromCoordinates, current, start, end);
                }
            }
            else if (angleType == HexAngleType.Wide)
            {
                start = unitDirection.Opposite ().Previous2 ();
                end = unitDirection;
                if (canSeeTruWalls)
                {
                    fromCoordinates = LookLimitedTruWalls (range, fromCoordinates, current, start, end);
                }
                else
                {
                    fromCoordinates = LookLimited (range, fromCoordinates, current, start, end);
                }
            }
            else
            {
                if (canSeeTruWalls)
                {
                    fromCoordinates = LookUnlimitedTruWalls (range, fromCoordinates, current);
                }
                else
                {
                    fromCoordinates = LookUnlimited (range, fromCoordinates, current);
                }
            }

        }
        return visibleCells;
    }

    private HexCoordinates LookLimitedTruWalls (int visionRange, HexCoordinates fromCoordinates, HexCell current, HexDirection start, HexDirection end)
    {
        HexDirection d = start;
        for (; d != end; d = d.Next ())
        {
            HexCell neighbor = current.GetNeighbor (d);
            if (
                neighbor == null ||
                neighbor.SearchPhase > searchFrontierPhase ||
                !neighbor.Explorable
            )
            {
                continue;
            }

            int distance = current.Distance + 1;
            if (distance + neighbor.ViewElevation > visionRange ||
                distance > fromCoordinates.DistanceTo (neighbor.coordinates)
            )
            {
                continue;
            }

            if (neighbor.SearchPhase < searchFrontierPhase)
            {
                neighbor.SearchPhase = searchFrontierPhase;
                neighbor.Distance = distance;
                neighbor.SearchHeuristic = 0;
                searchFrontier.Enqueue (neighbor);
            }
            else if (distance < neighbor.Distance)
            {
                int oldPriority = neighbor.SearchPriority;
                neighbor.Distance = distance;
                searchFrontier.Change (neighbor, oldPriority);
            }
        }

        return fromCoordinates;
    }
    private HexCoordinates LookLimited (int visionRange, HexCoordinates fromCoordinates, HexCell current, HexDirection start, HexDirection end)
    {
        HexDirection d = start;
        for (; d != end; d = d.Next ())
        {
            HexCell neighbor = current.GetNeighbor (d);
            if (
                neighbor == null ||
                neighbor.SearchPhase > searchFrontierPhase ||
                !neighbor.Explorable ||
                (!current.Walled && neighbor.Walled && !current.HasRoadThroughEdge (d))
            )
            {
                continue;
            }

            int distance = current.Distance + 1;
            if (distance + neighbor.ViewElevation > visionRange ||
                distance > fromCoordinates.DistanceTo (neighbor.coordinates)
            )
            {
                continue;
            }

            if (neighbor.SearchPhase < searchFrontierPhase)
            {
                neighbor.SearchPhase = searchFrontierPhase;
                neighbor.Distance = distance;
                neighbor.SearchHeuristic = 0;
                searchFrontier.Enqueue (neighbor);
            }
            else if (distance < neighbor.Distance)
            {
                int oldPriority = neighbor.SearchPriority;
                neighbor.Distance = distance;
                searchFrontier.Change (neighbor, oldPriority);
            }
        }

        return fromCoordinates;
    }

    private HexCoordinates LookUnlimitedTruWalls (int visionRange, HexCoordinates fromCoordinates, HexCell current)
    {
        for (HexDirection d = HexDirection.NE; d <= HexDirection.NW; d++)
        {
            HexCell neighbor = current.GetNeighbor (d);
            if (
                neighbor == null ||
                neighbor.SearchPhase > searchFrontierPhase ||
                !neighbor.Explorable ||
                neighbor.Walled
            )
            {
                continue;
            }

            int distance = current.Distance + 1;
            if (distance + neighbor.ViewElevation > visionRange ||
                distance > fromCoordinates.DistanceTo (neighbor.coordinates)
            )
            {
                continue;
            }

            if (neighbor.SearchPhase < searchFrontierPhase)
            {
                neighbor.SearchPhase = searchFrontierPhase;
                neighbor.Distance = distance;
                neighbor.SearchHeuristic = 0;
                searchFrontier.Enqueue (neighbor);
            }
            else if (distance < neighbor.Distance)
            {
                int oldPriority = neighbor.SearchPriority;
                neighbor.Distance = distance;
                searchFrontier.Change (neighbor, oldPriority);
            }
        }

        return fromCoordinates;
    }

    private HexCoordinates LookUnlimited (int visionRange, HexCoordinates fromCoordinates, HexCell current)
    {
        for (HexDirection d = HexDirection.NE; d <= HexDirection.NW; d++)
        {
            HexCell neighbor = current.GetNeighbor (d);
            if (
                neighbor == null ||
                neighbor.SearchPhase > searchFrontierPhase ||
                !neighbor.Explorable
            )
            {
                continue;
            }

            int distance = current.Distance + 1;
            if (distance + neighbor.ViewElevation > visionRange ||
                distance > fromCoordinates.DistanceTo (neighbor.coordinates)
            )
            {
                continue;
            }

            if (neighbor.SearchPhase < searchFrontierPhase)
            {
                neighbor.SearchPhase = searchFrontierPhase;
                neighbor.Distance = distance;
                neighbor.SearchHeuristic = 0;
                searchFrontier.Enqueue (neighbor);
            }
            else if (distance < neighbor.Distance)
            {
                int oldPriority = neighbor.SearchPriority;
                neighbor.Distance = distance;
                searchFrontier.Change (neighbor, oldPriority);
            }
        }

        return fromCoordinates;
    }

    public void IncreaseVisibility (HexCell fromCell, HexUnit unit)
    {
        if (!unit.team.PlayerShareVisibility ())
        {
            return;
        }
        List<HexCell> cells = GetVisibleCells (fromCell, unit);
        for (int i = 0; i < cells.Count; i++)
        {
            cells[i].IncreaseVisibility ();
        }
        ListPool<HexCell>.Add (cells);
    }

    public void DecreaseVisibility (HexCell fromCell, HexUnit unit)
    {
        List<HexCell> cells = GetVisibleCells (fromCell, unit);
        for (int i = 0; i < cells.Count; i++)
        {
            cells[i].DecreaseVisibility ();
        }
        ListPool<HexCell>.Add (cells);
    }

    public void ResetVisibility ()
    {
        for (int i = 0; i < cells.Length; i++)
        {
            cells[i].ResetVisibility ();
        }
    }

    public void ResetExploration ()
    {
        for (int i = 0; i < cells.Length; i++)
        {
            cells[i].ResetExploration ();
        }
    }

    public HexCell GetCell (Ray ray)
    {
        RaycastHit hit;
        if (Physics.Raycast (ray, out hit))
        {
            return GetCell (hit.point);
        }
        return null;
    }

    public void EndTurn ()
    {
        StopAllCoroutines ();
    }

    public void RemoveUnit (HexUnit unit)
    {
        unit.Die ();
    }

    public void ShowUI (bool visible)
    {
        for (int i = 0; i < chunks.Length; i++)
        {
            chunks[i].ShowUI (visible);
        }
    }

    public void Refresh ()
    { }

    #region Save Load

    public void Save (BinaryWriter writer)
    {
        writer.Write (cellCountX);
        writer.Write (cellCountZ);

        for (int i = 0; i < cells.Length; i++)
        {
            cells[i].Save (writer);
        }
    }

    public void Load (BinaryReader reader, int header)
    {
        ClearPath ();
        int x = reader.ReadInt32 ();
        int z = reader.ReadInt32 ();
        Init (x, z);
        if (x != cellCountX || z != cellCountZ)
        {
            if (!CreateMap (x, z))
            {
                return;
            }
        }
        bool originalImmediateMode = cellShaderData.ImmediateMode;

        cellShaderData.ImmediateMode = true;

        for (int i = 0; i < cells.Length; i++)
        {
            cells[i].Load (reader, header);
        }
        for (int i = 0; i < chunks.Length; i++)
        {
            chunks[i].Refresh ();
        }

        cellShaderData.ImmediateMode = originalImmediateMode;
    }

    #endregion
}