using UnityEngine;
using Unity.Collections;
using Unity.Jobs;
using WraithavenGames.Bones3.BlockProperties;
using System.Collections.Generic;
using WraithavenGames.Bones3;

namespace WraithavenGames.Bones3.Meshing
{
    /// <summary>
    /// This class represents a chunk which is being remeshed.
    /// </summary>
    public class ChunkRemeshOperation
    {
        private List<IRemeshTask> tasks = new List<IRemeshTask>();
        private ChunkTaskPool pool;

        private Chunk chunk;
        private Material[] materials;

        public ChunkRemeshOperation(ChunkTaskPool pool)
        {
            this.pool = pool;
        }

        public void Init(Chunk chunk)
        {
            if (this.chunk != null)
                Finish();

            this.chunk = chunk;

            var collectBlocks = pool.Get<CollectBlocksTask>();
            collectBlocks.Schedule(chunk);
            tasks.Add(collectBlocks);

            int materialCount = collectBlocks.GetBlockTypes().Count;
            if (collectBlocks.GetBlockTypes().Contains(0))
                materialCount--;

            materials = new Material[materialCount];
            int materialIndex = 0;

            foreach (ushort blockId in collectBlocks.GetBlockTypes())
            {
                if (blockId == 0)
                    continue;

                MaterialBlock blockState = chunk.BlockTypes.GetMaterialProperties(blockId);
                materials[materialIndex] = blockState.Material;

                var collectQuads = pool.Get<CollectQuadsTask>();
                collectQuads.Schedule(collectBlocks, blockId);
                tasks.Add(collectQuads);

                var combineQuads = pool.Get<CombineQuadsTask>();
                combineQuads.Schedule(collectQuads, materialIndex);
                tasks.Add(combineQuads);

                materialIndex++;
            }

            var collectColBlocks = pool.Get<CollectColQuadsTask>();
            collectColBlocks.Schedule(collectBlocks);
            tasks.Add(collectColBlocks);

            var combineColQuads = pool.Get<CombineQuadsTask>();
            combineColQuads.Schedule(collectColBlocks, -1);
            tasks.Add(combineColQuads);

            JobHandle.ScheduleBatchedJobs();
        }

        public void Finish()
        {
            foreach (var t in tasks)
                t.Complete();

            UpdateChunkCollision();
            UpdateChunkVisuals();

            chunk = null;
            foreach (var t in tasks)
                pool.Put(t);
            tasks.Clear();
        }

        private int[] GetSubMeshSizes(List<IRemeshTask> quadTasks)
        {
            int[] submeshSizes = new int[quadTasks.Count];

            for (int i = 0; i < quadTasks.Count; i++)
            {
                var q = quadTasks[i] as CombineQuadsTask;

                int index = q.GetMaterialIndex();
                if (index == -1)
                    index = 0;

                submeshSizes[index] = q.GetQuadCount()[0];
            }

            return submeshSizes;
        }

        private void UpdateChunkVisuals()
        {
            MeshFilter meshFilter = chunk.GetComponent<MeshFilter>();
            Mesh mesh = meshFilter.sharedMesh;

            if (mesh == null)
                mesh = new Mesh();
            else
                mesh.Clear();

            List<IRemeshTask> quadMatTasks = tasks.FindAll(t => t is CombineQuadsTask && (t as CombineQuadsTask).GetMaterialIndex() >= 0);
            int[] submeshSizes = GetSubMeshSizes(quadMatTasks);
            int vertexCount = Sum(submeshSizes, submeshSizes.Length) * 4;

            mesh.vertices = GetVertices(quadMatTasks, vertexCount);
            // mesh.uv = new Vector2[0];
            mesh.subMeshCount = submeshSizes.Length;

            for (int i = 0; i < submeshSizes.Length; i++)
                mesh.SetTriangles(GetTriangles(submeshSizes, i), i, true, Sum(submeshSizes, i));

            mesh.RecalculateNormals();
            mesh.RecalculateTangents();

            meshFilter.sharedMesh = null;
            meshFilter.sharedMesh = mesh;

            MeshRenderer renderer = chunk.GetComponent<MeshRenderer>();
            renderer.sharedMaterials = materials;
        }

        private void UpdateChunkCollision()
        {
            MeshCollider collider = chunk.GetComponent<MeshCollider>();
            Mesh mesh = collider.sharedMesh;

            if (mesh == null)
                mesh = new Mesh();
            else
                mesh.Clear();

            List<IRemeshTask> quadColTasks = tasks.FindAll(t => t is CombineQuadsTask && (t as CombineQuadsTask).GetMaterialIndex() < 0);
            int[] submeshSizes = GetSubMeshSizes(quadColTasks);

            mesh.vertices = GetVertices(quadColTasks, submeshSizes[0] * 4);
            mesh.triangles = GetTriangles(new int[] { submeshSizes[0] }, 0);

            mesh.RecalculateNormals();

            collider.sharedMesh = null;
            collider.sharedMesh = mesh;
        }

        private Vector3[] GetVertices(List<IRemeshTask> quadTasks, int vertexCount)
        {
            Vector3[] vertices = new Vector3[vertexCount];
            int vertexPos = 0;

            foreach (IRemeshTask quadTask in quadTasks)
            {
                var task = quadTask as CombineQuadsTask;
                var raw = task.GetQuads();
                int count = task.GetQuadCount()[0];
                for (int i = 0; i < count; i++)
                {
                    Quad quad = raw[i];
                    switch (quad.side)
                    {
                        case 0:
                            {
                                float sx = quad.offset;
                                float sy = quad.x;
                                float sz = quad.y;
                                float bx = sx + 1;
                                float by = sy + quad.w;
                                float bz = sz + quad.h;

                                vertices[vertexPos++] = new Vector3(bx, by, bz);
                                vertices[vertexPos++] = new Vector3(bx, sy, bz);
                                vertices[vertexPos++] = new Vector3(bx, sy, sz);
                                vertices[vertexPos++] = new Vector3(bx, by, sz);
                                break;
                            }

                        case 1:
                            {
                                float sx = quad.offset;
                                float sy = quad.x;
                                float sz = quad.y;
                                float by = sy + quad.w;
                                float bz = sz + quad.h;

                                vertices[vertexPos++] = new Vector3(sx, sy, sz);
                                vertices[vertexPos++] = new Vector3(sx, sy, bz);
                                vertices[vertexPos++] = new Vector3(sx, by, bz);
                                vertices[vertexPos++] = new Vector3(sx, by, sz);
                                break;
                            }

                        case 2:
                            {
                                float sx = quad.x;
                                float sy = quad.offset;
                                float sz = quad.y;
                                float bx = sx + quad.w;
                                float by = sy + 1;
                                float bz = sz + quad.h;

                                vertices[vertexPos++] = new Vector3(sx, by, sz);
                                vertices[vertexPos++] = new Vector3(sx, by, bz);
                                vertices[vertexPos++] = new Vector3(bx, by, bz);
                                vertices[vertexPos++] = new Vector3(bx, by, sz);
                                break;
                            }

                        case 3:
                            {
                                float sx = quad.x;
                                float sy = quad.offset;
                                float sz = quad.y;
                                float bx = sx + quad.w;
                                float bz = sz + quad.h;

                                vertices[vertexPos++] = new Vector3(bx, sy, bz);
                                vertices[vertexPos++] = new Vector3(sx, sy, bz);
                                vertices[vertexPos++] = new Vector3(sx, sy, sz);
                                vertices[vertexPos++] = new Vector3(bx, sy, sz);
                                break;
                            }

                        case 4:
                            {
                                float sx = quad.x;
                                float sy = quad.y;
                                float sz = quad.offset;
                                float bx = sx + quad.w;
                                float by = sy + quad.h;
                                float bz = sz + 1;

                                vertices[vertexPos++] = new Vector3(bx, by, bz);
                                vertices[vertexPos++] = new Vector3(sx, by, bz);
                                vertices[vertexPos++] = new Vector3(sx, sy, bz);
                                vertices[vertexPos++] = new Vector3(bx, sy, bz);
                                break;
                            }

                        case 5:
                            {
                                float sx = quad.x;
                                float sy = quad.y;
                                float sz = quad.offset;
                                float bx = sx + quad.w;
                                float by = sy + quad.h;

                                vertices[vertexPos++] = new Vector3(sx, sy, sz);
                                vertices[vertexPos++] = new Vector3(sx, by, sz);
                                vertices[vertexPos++] = new Vector3(bx, by, sz);
                                vertices[vertexPos++] = new Vector3(bx, sy, sz);
                                break;
                            }
                    }
                }
            }

            return vertices;
        }

        private int[] GetTriangles(int[] submeshSizes, int index)
        {
            int quadCount = submeshSizes[index];
            int[] triangles = new int[quadCount * 6];
            for (int i = 0; i < quadCount; i++)
            {
                triangles[i * 6 + 0] = i * 4 + 0;
                triangles[i * 6 + 1] = i * 4 + 1;
                triangles[i * 6 + 2] = i * 4 + 2;
                triangles[i * 6 + 3] = i * 4 + 0;
                triangles[i * 6 + 4] = i * 4 + 2;
                triangles[i * 6 + 5] = i * 4 + 3;
            }

            return triangles;
        }

        private int Sum(int[] n, int length)
        {
            int v = 0;

            for (int i = 0; i < length; i++)
                v += n[i];

            return v;
        }
    }
}