import { TaskFrame } from "@/components/store/task-frame";

/** Pages here sit in the workspace frame; a buyer's own task pages open in the storefront for non-staff buyers (G-037). */
export default function Layout({ children }: { children: React.ReactNode }) { return <TaskFrame>{children}</TaskFrame>; }
