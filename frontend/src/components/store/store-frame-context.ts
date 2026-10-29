import { createContext, useContext } from 'react';

/** True inside the storefront frame. Workspace navigation (sidebar, topbar, palette, skip target) never renders there (G-037). */
export const StoreFrameContext = createContext(false);
export function useInStoreFrame() { return useContext(StoreFrameContext); }
