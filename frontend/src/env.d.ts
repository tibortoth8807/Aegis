interface ImportMetaEnv {
  /** Base URL of the .NET API. Empty = the in-browser demo API. */
  readonly VITE_API_URL?: string;
}
interface ImportMeta {
  readonly env: ImportMetaEnv;
}
