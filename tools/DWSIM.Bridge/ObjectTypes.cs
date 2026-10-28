using DWSIM.Interfaces;
using DWSIM.Interfaces.Enums.GraphicObjects;

namespace DWSIM.Bridge
{
    /// <summary>The type name the frontend and the editor catalog know an object by.</summary>
    public static class ObjectTypes
    {
        /// <summary>
        /// Mixers and splitters keep the drawing's historical node types (NodeIn, NodeOut) on
        /// their graphic objects, though they are added as Mixer and Splitter; every other type
        /// is the graphic object's own.
        /// </summary>
        public static string Name(ObjectType type)
        {
            switch (type)
            {
                case ObjectType.NodeIn: return nameof(ObjectType.Mixer);
                case ObjectType.NodeOut: return nameof(ObjectType.Splitter);
                case ObjectType.NodeEn: return nameof(ObjectType.EnergyMixer);
                default: return type.ToString();
            }
        }

        public static string Name(ISimulationObject obj)
            => obj.GraphicObject != null ? Name(obj.GraphicObject.ObjectType) : obj.GetType().Name;
    }
}
