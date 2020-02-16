using System;
using System.Collections.Generic;
using System.Linq;
using GUI.Utils;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.ResourceTypes.ModelAnimation;

namespace GUI.Types.Renderer
{
    internal class ModelRenderer : IMeshRenderer
    {
        public Model Model { get; }

        private readonly VrfGuiContext guiContext;

        private List<Animation> animations = new List<Animation>();
        private List<Material> materials = new List<Material>();

        private List<MeshRenderer> meshRenderers = new List<MeshRenderer>();

        public ModelRenderer(Model model, VrfGuiContext vrfGuiContext)
        {
            Model = model;

            guiContext = vrfGuiContext;

            // Load required resources
            LoadAnimations();
            LoadMaterials();
            LoadMeshes();
            LoadSkeleton();
        }

        public void Update(float frameTime)
        {
        }

        public void Render(Camera camera)
        {
            foreach (var meshRenderer in meshRenderers)
            {
                meshRenderer.Render(camera);
            }
        }

        public IEnumerable<string> GetSupportedRenderModes()
            => meshRenderers.SelectMany(renderer => renderer.GetSupportedRenderModes()).Distinct();

        public void SetRenderMode(string renderMode)
        {
            foreach (var renderer in meshRenderers)
            {
                renderer.SetRenderMode(renderMode);
            }
        }

        private void LoadMeshes()
        {
            // Get embedded meshes
            foreach (var embeddedMesh in Model.GetEmbeddedMeshes())
            {
                meshRenderers.Add(new MeshRenderer(embeddedMesh, guiContext));
            }

            // Load referred meshes from file
            var referredMeshNames = Model.GetReferredMeshNames();
            foreach (var refMesh in referredMeshNames)
            {
                var newResource = guiContext.LoadFileByAnyMeansNecessary(refMesh + "_c");
                if (newResource == null)
                {
                    Console.WriteLine("unable to load mesh " + refMesh);
                    continue;
                }

                if (!newResource.ContainsBlockType(BlockType.VBIB))
                {
                    Console.WriteLine("Old style model, no VBIB!");
                    continue;
                }

                meshRenderers.Add(new MeshRenderer(new Mesh(newResource), guiContext));
            }
        }

        private void LoadMaterials()
        {
            /*var materialGroups = Model.GetData().GetArray<IKeyValueCollection>("m_materialGroups");

            foreach (var materialGroup in materialGroups)
            {
                if (materialGroup.GetProperty<string>("m_name") == skin)
                {
                    var materials = materialGroup.GetArray<string>("m_materials");
                    skinMaterials.AddRange(materials);
                    break;
                }
            }*/
        }

        private void LoadSkeleton()
        {
            Model.GetSkeleton();
        }

        private void LoadAnimations()
        {
            var animGroupPaths = Model.GetData().GetArray<string>("m_refAnimGroups");
            foreach (var animGroupPath in animGroupPaths)
            {
                var animGroup = guiContext.LoadFileByAnyMeansNecessary(animGroupPath + "_c");
                animations.AddRange(AnimationGroupLoader.LoadAnimationGroup(animGroup, guiContext));
            }
        }
    }
}
