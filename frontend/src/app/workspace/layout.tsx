import { WorkspaceFrame } from "@/components/workspace-kit/workspace-frame";

/** The cooperative staff's home: their work queue and destinations inside the back-office frame. */
export default function Layout({ children }: { children: React.ReactNode }) { return <WorkspaceFrame>{children}</WorkspaceFrame>; }
