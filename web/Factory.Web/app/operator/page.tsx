"use client";

import { usePathname } from "next/navigation";
import { AssistantConversationPanel } from "@/components/assistant-drawer";

export default function OperatorPage() {
  const pathname = usePathname();
  return <div className="mx-auto flex h-full min-h-0 w-full max-w-4xl flex-col gap-4">
    <AssistantConversationPanel mode="page" pathname={pathname} />
  </div>;
}
