using UnityEngine;
using Unity.Collections;
using Unity.Jobs;
using System.Collections.Generic;
using WraithavenGames.Bones3.BlockProperties;

namespace WraithavenGames.Bones3.Meshing
{
    /// <summary>
    /// The <c>ChunkRemesher</c> is a MonoBehavior which acts as a remesh manager. It simply passes
    /// along chunk remesh requests to the job system, and each frame, once the requests are complete,
    /// the corresponding chunks are updated in Unity.
    /// </summary>
    public class ChunkRemesher
    {
        /// <summary>
        /// This constant represents the maximum number of quads that can be generated by a chunk in
        /// the worst case senario. This value is used for allocating memory.
        /// </summary>
        private const int MAX_QUADS = 16 * 16 * 8 * 6;

        public void Remesh(Chunk chunk)
        {
            var blocks = new NativeArray<ushort>(4096, Allocator.TempJob);
            var blockRef = new List<ushort>();

            for (int i = 0; i < 4096; i++)
            {
                blocks[i] = chunk.GetBlockID(i);

                if (!blockRef.Contains(blocks[i]))
                    blockRef.Add(blocks[i]);
            }

            BlockID[] blockIDs = new BlockID[blockRef.Count];
            for (int i = 0; i < blockIDs.Length; i++)
            {
                MaterialBlock blockState = chunk.GetBlockState(i);

                BlockID id = new BlockID();
                blockIDs[i] = id;
                id.id = blocks[i];

                if (blockState == null)
                {
                    id.hasCollision = 0;
                    id.transparent = 1;
                    id.viewInsides = 0;
                    id.depthSort = 0;
                }
                else
                {
                    id.hasCollision = (byte)(blocks[i] > 0 ? 0 : 1);
                    id.transparent = (byte)(blockState.Transparent ? 1 : 0);
                    id.viewInsides = (byte)(blockState.ViewInsides ? 1 : 0);
                    id.depthSort = (byte)(blockState.DepthSort ? 1 : 0);
                }

                blockRef.Add(blocks[i]);
            }
            var blockProperties = new NativeArray<BlockID>(blockIDs, Allocator.TempJob);

            List<RemeshStub> stubs = new List<RemeshStub>();
            CreateCollisionRemeshStub(stubs, blocks, blockProperties);
            CreateMaterialRemeshStubs(blockIDs, stubs, blocks, blockProperties, chunk);

            JobHandle.ScheduleBatchedJobs();

            foreach (var s in stubs)
                s.StopJob();

            UpdateCollision(chunk, stubs);
            UpdateVisuals(chunk, stubs);

            blocks.Dispose();
            blockProperties.Dispose();

            foreach (var stub in stubs)
                stub.Dispose();
        }

        private void CreateCollisionRemeshStub(List<RemeshStub> stubs, NativeArray<ushort> blocks, NativeArray<BlockID> blockProperties)
        {
            var vertices = new NativeArray<Vector3>(MAX_QUADS * 4, Allocator.TempJob);
            var normals = new NativeArray<Vector3>(MAX_QUADS * 4, Allocator.TempJob);
            var triangles = new NativeArray<ushort>(MAX_QUADS * 6, Allocator.TempJob);
            var count = new NativeArray<int>(2, Allocator.TempJob);

            RemeshCollisionJob remeshCollision = new RemeshCollisionJob(blocks, blockProperties, vertices, normals, triangles, count);
            JobHandle job = remeshCollision.Schedule();

            stubs.Add(new RemeshStub(count, vertices, normals, triangles, job));
        }

        private void CreateMaterialRemeshStubs(BlockID[] blockIDs, List<RemeshStub> stubs, NativeArray<ushort> blocks,
            NativeArray<BlockID> blockProperties, Chunk chunk)
        {
            for (int i = 0; i < blockIDs.Length; i++)
            {
                ushort id = blockIDs[i].id;

                var vertices = new NativeArray<Vector3>(MAX_QUADS * 4, Allocator.TempJob);
                var normals = new NativeArray<Vector3>(MAX_QUADS * 4, Allocator.TempJob);
                var uvs = new NativeArray<Vector2>(MAX_QUADS * 4, Allocator.TempJob);
                var triangles = new NativeArray<ushort>(MAX_QUADS * 6, Allocator.TempJob);
                var count = new NativeArray<int>(2, Allocator.TempJob);

                RemeshMaterialJob remeshMaterial = new RemeshMaterialJob(blocks, blockProperties, id, vertices, normals, uvs, triangles, count);
                JobHandle job = remeshMaterial.Schedule();

                Material material = chunk.BlockTypes.GetMaterialProperties(id)?.Material;
                stubs.Add(new RemeshStubMaterial(count, vertices, normals, uvs, triangles, material, job));
            }
        }

        private void UpdateCollision(Chunk chunk, List<RemeshStub> stubs)
        {
            MeshCollider meshCollider = chunk.GetComponent<MeshCollider>();
            Mesh colMesh = meshCollider.sharedMesh;

            if (colMesh == null)
                colMesh = new Mesh();
            else
                colMesh.Clear();

            List<Vector3> vertices = new List<Vector3>();
            List<Vector3> normals = new List<Vector3>();
            List<int> triangles = new List<int>();

            RemeshStub stub = stubs.Find(s => !(s is RemeshStubMaterial));
            stub.GetVertices(vertices);
            stub.GetNormals(normals);
            stub.GetTriangles(triangles);

            colMesh.SetVertices(vertices);
            colMesh.SetNormals(normals);
            colMesh.SetTriangles(triangles, 0);

            meshCollider.sharedMesh = null;
            meshCollider.sharedMesh = colMesh;
        }

        private void UpdateVisuals(Chunk chunk, List<RemeshStub> stubs)
        {
            MeshFilter meshFilter = chunk.GetComponent<MeshFilter>();
            Mesh mesh = meshFilter.sharedMesh;

            if (mesh == null)
                mesh = new Mesh();
            else
                mesh.Clear();

            List<Vector3> vertices = new List<Vector3>();
            List<Vector3> normals = new List<Vector3>();
            List<Vector2> uvs = new List<Vector2>();

            List<SubMesh> submeshes = new List<SubMesh>();

            foreach (var stub in stubs.FindAll(s => s is RemeshStubMaterial))
            {
                RemeshStubMaterial s = stub as RemeshStubMaterial;

                List<int> triangles = new List<int>();

                submeshes.Add(new SubMesh()
                {
                    triangles = triangles,
                    vertexOffset = vertices.Count,
                    material = s.Material
                });

                s.GetVertices(vertices);
                s.GetNormals(normals);
                s.GetUVs(uvs);
                s.GetTriangles(triangles);
            }

            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);

            Material[] materials = new Material[submeshes.Count];

            for (int i = 0; i < submeshes.Count; i++)
            {
                mesh.SetTriangles(submeshes[i].triangles, i, true, submeshes[i].vertexOffset);
                materials[i] = submeshes[i].material;
            }

            meshFilter.sharedMesh = null;
            meshFilter.sharedMesh = mesh;

            MeshRenderer renderer = chunk.GetComponent<MeshRenderer>();
            renderer.sharedMaterials = materials;
        }
    }

    /// <summary>
    /// A temporary data holder for storing information used to create a submesh.
    /// </summary>
    struct SubMesh
    {
        public List<int> triangles;
        public Material material;
        public int vertexOffset;
    }

    /// <summary>
    /// A remesh stub is a single chunk mesh which is generated by a remesh job. As each chunk can
    /// contain multiple meshes, (one for each block material plus one for the collision), the chunk
    /// contains multiple corresponding remesh stubs.
    /// 
    /// The base <c>RemeshStub</c> is used for generating the collision mesh while the
    /// <c>RemeshStubMaterial</c> is used for generating material submeshes.
    /// </summary>
    class RemeshStub
    {
        /// <summary>
        /// A list of vertex locations for this mesh. If empty, this vertex data object represents an
        /// empty mesh.
        /// </summary>
        protected NativeArray<Vector3> vertices;

        /// <summary>
        /// A list of normal values for this mesh.
        /// </summary>
        protected NativeArray<Vector3> normals;

        /// <summary>
        /// A list of vertex indices, representing the triangles for this mesh. Each triplet of indices
        /// represents a single triangle.
        /// </summary>
        protected NativeArray<ushort> triangles;

        /// <summary>
        /// An array containing exactly values. The first value is the number of vertices which were
        /// generated by the remesher and the second value is the number of triangles which were
        /// generated by the remesher.
        /// </summary>
        protected NativeArray<int> count;

        /// <summary>
        /// The job handle which this stub is waiting on.
        /// </summary>
        protected JobHandle job;

        /// <summary>
        /// Checks if this job has finished or not.
        /// </summary>
        /// <value>True if the job has finished. False otherwise.</value>
        public bool IsDone { get { return job.IsCompleted; } }

        /// <summary>
        /// Gets the number of vertices that were generated in this mesh.
        /// </summary>
        /// <value>The number of vertices.</value>
        protected int VertexCount { get { return count[0]; } }

        /// <summary>
        /// Gets the number of triangles that were generated in this mesh.
        /// </summary>
        /// <value>The number of triangles.</value>
        protected int TriangleCount { get { return count[1]; } }

        /// <summary>
        /// Creates a new <c>RemeshStub</c> object.
        /// </summary>
        /// <param name="count">A returned list of values containing the size of the mesh.</param>
        /// <param name="vertices">A list of vertices which will be generated.</param>
        /// <param name="normals">A list of normals which will be generated.</param>
        /// <param name="triangles">A list of triangles which will be generated.</param>
        /// <param name="job">The job this stub is wrapping.</param>
        public RemeshStub(NativeArray<int> count, NativeArray<Vector3> vertices, NativeArray<Vector3> normals,
            NativeArray<ushort> triangles, JobHandle job)
        {
            this.count = count;
            this.vertices = vertices;
            this.normals = normals;
            this.triangles = triangles;
            this.job = job;
        }

        /// <summary>
        /// Disposes all native resources associated with this stub.
        /// </summary>
        public virtual void Dispose()
        {
            count.Dispose();
            vertices.Dispose();
            normals.Dispose();
            triangles.Dispose();
        }

        /// <summary>
        /// Gets all the vertices in this stub and appends them to the end of the given vertex list.
        /// </summary>
        /// <param name="vertices">The list of vertices to add to.</param>
        public void GetVertices(List<Vector3> vertices)
        {
            int c = VertexCount;
            vertices.Capacity = Mathf.Max(vertices.Capacity, vertices.Count + c);

            for (int i = 0; i < c; i++)
                vertices.Add(this.vertices[i]);
        }

        /// <summary>
        /// Gets all the normals in this stub and appends them to the end of the given normal list.
        /// </summary>
        /// <param name="normals">The list of normals to add to.</param>
        public void GetNormals(List<Vector3> normals)
        {
            int c = VertexCount;
            normals.Capacity = Mathf.Max(normals.Capacity, normals.Count + c);

            for (int i = 0; i < c; i++)
                normals.Add(this.normals[i]);
        }

        /// <summary>
        /// Gets all the triangles in this stub and appends them to the end of the given triangle list.
        /// </summary>
        /// <param name="triangles">The list of triangles to add to.</param>
        public void GetTriangles(List<int> triangles)
        {
            int c = TriangleCount * 3;
            triangles.Capacity = Mathf.Max(triangles.Capacity, triangles.Count + c);

            for (int i = 0; i < c; i++)
                triangles.Add((int)this.triangles[i]);
        }

        /// <summary>
        /// Waits for the attached job to finish, this value can be called to ensure the job is properly
        /// completed.
        /// </summary>
        public void StopJob()
        {
            job.Complete();
        }
    }

    /// <summary>
    /// A <c>RemeshStubMaterial</c> is an extention of a <c>RemeshStub</c> which adds support for handling
    /// a material submesh.
    /// </summary>
    class RemeshStubMaterial : RemeshStub
    {
        /// <summary>
        /// A list of uv values for this mesh.
        /// </summary>
        protected NativeArray<Vector2> uvs;

        /// <summary>
        /// The material which this submesh represents.
        /// </summary>
        protected Material material;

        /// <summary>
        /// Gets the material associated with the remesh stub.
        /// </summary>
        /// <value>The material.</value>
        public Material Material { get { return material; } }

        /// <summary>
        /// Creates a new <c>RemeshStubMaterial</c> object.
        /// </summary>
        /// <param name="count">A returned list of values containing the size of the mesh.</param>
        /// <param name="vertices">A list of vertices which will be generated.</param>
        /// <param name="normals">A list of normals which will be generated.</param>
        /// <param name="uvs">A list of uvs which will be generated.</param>
        /// <param name="triangles">A list of triangles which will be generated.</param>
        /// <param name="material">The material of the submesh this stub is generating.</param>
        /// <param name="job">The job this stub is wrapping.</param>
        public RemeshStubMaterial(NativeArray<int> count, NativeArray<Vector3> vertices, NativeArray<Vector3> normals,
            NativeArray<Vector2> uvs, NativeArray<ushort> triangles, Material material,
            JobHandle job) : base(count, vertices, normals, triangles, job)
        {
            this.uvs = uvs;
            this.material = material;
        }

        /// <summary>
        /// Disposes all native resources associated with this stub.
        /// </summary>
        public override void Dispose()
        {
            base.Dispose();
            uvs.Dispose();
        }

        /// <summary>
        /// Gets all the uvs in this stub and appends them to the end of the given uv list.
        /// </summary>
        /// <param name="uvs">The list of uvs to add to.</param>
        public void GetUVs(List<Vector2> uvs)
        {
            int c = VertexCount;
            uvs.Capacity = Mathf.Max(uvs.Capacity, uvs.Count + c);

            for (int i = 0; i < c; i++)
                uvs.Add(this.uvs[i]);
        }
    }
}