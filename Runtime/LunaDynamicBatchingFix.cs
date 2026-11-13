using UnityEngine;

namespace SupercentVN.LunaTools
{
    /// <summary>
    /// Works around a Luna (7.2) dynamic batching bug that garbles batched meshes on the web build.
    ///
    /// Luna appends a per-vertex matrix index (ATTR0) after the combined vertex data of a batch group.
    /// <c>DynamicBatchGroup.addMatrixIndexAttribute</c> takes the vertex format from a pool shared by
    /// every group with the same vertex layout and overwrites its ATTR0 offset in place. Vertex
    /// attributes are re-applied from that format on every draw, so a group that did not rebuild this
    /// frame reads its matrix indices at another group's offset: vertices pick random matrices and turn
    /// into huge stretched shapes, or vanish. Planar shadows (a 2nd material on the same mesh, so the
    /// same layout with a different vertex total) are hit first.
    ///
    /// The fix gives each group its own copy of the format with its own ATTR0 offset. It only exists in
    /// the Luna build (UNITY_LUNA), patches only when Luna's batcher has the expected shape, and does
    /// nothing in the Unity Editor.
    /// </summary>
    public static class LunaDynamicBatchingFix
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
#if UNITY_LUNA
            Bridge.Script.Write(@"
(function () {
  var TAG = '[LunaTools] Dynamic batching fix';
  try {
    var app = pc.Application.getApplication();
    var batcher = app && app.renderer && app.renderer.dynamicBatcher;
    if (!batcher || batcher.__lunaToolsFormatFix) return;
    batcher.__lunaToolsFormatFix = true;
    if (typeof batcher.createNewGroup !== 'function' || !batcher.formatPool) {
      console.warn(TAG + ' skipped: unknown Luna batcher.');
      return;
    }
    var patchGroupClass = function (group) {
      var proto = group && Object.getPrototypeOf(group);
      if (!proto || proto.__lunaToolsFormatFix) return;
      proto.__lunaToolsFormatFix = true;
      var original = proto.addMatrixIndexAttribute;
      if (typeof original !== 'function' || String(original).indexOf('formatPool') < 0) {
        console.warn(TAG + ' skipped: addMatrixIndexAttribute differs in this Luna version.');
        return;
      }
      proto.addMatrixIndexAttribute = function (format, dataBytes) {
        var shared = original.call(this, format, dataBytes);
        var attr = shared && shared.elementMap && shared.elementMap.ATTR0;
        if (!attr) return shared;
        var own = Object.assign({}, attr, { offset: dataBytes });
        var copy = Object.assign({}, shared);
        copy.elementMap = Object.assign({}, shared.elementMap);
        copy.elementMap.ATTR0 = own;
        copy.elements = shared.elements.map(function (element) { return element === attr ? own : element; });
        return copy;
      };
      console.log(TAG + ' installed.');
    };
    var createNewGroup = batcher.createNewGroup;
    batcher.createNewGroup = function () {
      var group = createNewGroup.apply(this, arguments);
      patchGroupClass(group);
      return group;
    };
    if (batcher.batchGroups && batcher.batchGroups.forEach) batcher.batchGroups.forEach(patchGroupClass);
  } catch (error) {
    console.warn(TAG + ' failed:', error);
  }
})();
");
#endif
        }

        /// <summary>
        /// Not used. Luna drops every class that no scene or asset references before compiling, and a
        /// [RuntimeInitializeOnLoadMethod] does not count as a reference; a file that declares an extension
        /// method is kept whole (Luna cannot trace extension calls), which keeps <see cref="Install"/>.
        /// </summary>
        internal static bool LunaKeepAlive(this Object target) => target != null;
    }
}
