import { WorkspaceFrame } from "@/components/workspace-kit/workspace-frame";

/** Every page in this area sits in the shared workspace frame (sidebar, menu, workspace search and help). */
export default function Layout({ children }: { children: React.ReactNode }) { return <WorkspaceFrame>{children}</WorkspaceFrame>; }
