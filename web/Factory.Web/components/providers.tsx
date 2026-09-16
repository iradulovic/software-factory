"use client";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { NuqsAdapter } from "nuqs/adapters/next/app";
import { useState } from "react";

export function Providers({ children }: { children: React.ReactNode }) {
  const [client] = useState(() => new QueryClient({ defaultOptions: { queries: { refetchInterval: 10_000, staleTime: 5_000 } } }));
  return <NuqsAdapter><QueryClientProvider client={client}>{children}</QueryClientProvider></NuqsAdapter>;
}
