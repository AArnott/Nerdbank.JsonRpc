// Custom NetWasm browser bootstrap: identical to the SDK's browser-raw-bootstrap.mjs,
// except that it lets the page supply [JSImport] consumer modules via
// globalThis.netwasmConsumerModules (the stock SDK browser bootstrap has no way to do this).
import { createBrowserBootstrapComposition } from "__NETWASM_HOSTING_DIR__browser-bootstrap-composition.mjs";
import { createBrowserExecutionComposition } from "__NETWASM_HOSTING_DIR__browser-execution-composition.mjs";
import { createBrowserRawLoader } from "__NETWASM_HOSTING_DIR__browser-raw-loader.mjs";
import { createManagedExceptionOutput } from "__NETWASM_HOSTING_DIR__managed-exception-output.mjs";
import { createRawExecutionStrategy } from "__NETWASM_HOSTING_DIR__raw-execution-strategy.mjs";
import { prepareRawNetWasmInterop } from "__NETWASM_HOSTING_DIR__raw-interop-preparation.mjs";
import { createSelectedArtifactStrategyResolver } from "__NETWASM_HOSTING_DIR__selected-artifact-strategy-resolver.mjs";

function createExecution(options) {
  return createBrowserExecutionComposition(
    options,
    value => createSelectedArtifactStrategyResolver(
      "raw",
      createRawExecutionStrategy(createBrowserRawLoader({
        manifestUrl: value.manifestUrl,
        platform: Object.freeze({
          compileCoreModule: value.platform.compileCoreModule,
          createModuleUrl: value.platform.createModuleUrl,
          digest: value.platform.digest,
          fetch: value.platform.fetch,
          importModule: value.platform.importModule,
          revokeModuleUrl: value.platform.revokeModuleUrl,
        }),
      }))),
    ({ consumerModules, manifest, stderr }) => prepareRawNetWasmInterop({
      consumerModules: { ...consumerModules, ...(globalThis.netwasmConsumerModules ?? {}) },
      manifest,
      runtimeModules: Object.freeze(Object.create(null)),
      managedExceptionReporting: createManagedExceptionOutput(stderr),
    }));
}

export function createBrowserNetWasmBootstrap(options) {
  return createBrowserBootstrapComposition(options, createExecution);
}
