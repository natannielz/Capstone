"use client";

import { StatusBadge } from "@/components/workspace-kit/status-badge";
import type { Actor } from "@/lib/domain/accounts";
import type { State,Order,CommandResult } from "@/lib/domain/model";
import { orderFulfillmentLabel } from "@/lib/domain/order-views";
import type { QueryPatch } from "./use-workspace-navigation";

export type Page="dashboard"|"catalog"|"orders"|"deliveries"|"stock"|"billing"|"payments"|"procurement"|"reports"|"periods"|"admin"|"profile";
export type FormField={key:string;label:string;type?:"text"|"number"|"date"|"textarea"|"select";value?:string|number;min?:number;max?:number;options?:{value:string;label:string}[];optional?:boolean;group?:string};
export type ActionSpec={title:string;description:string;type:string;fields:FormField[];data?:Record<string,unknown>;map?:(values:Record<string,string>)=>Record<string,unknown>;label?:string;after?:(r:CommandResult)=>void};
export type WorkspaceContext={refresh:()=>Promise<void>;s:State;actor:Actor;go:(page:Page,id?:string,query?:QueryPatch)=>void;ask:(spec:ActionSpec)=>void};
export function orderStatus(s:State,o:Order){return orderFulfillmentLabel(s,o);}
export function Status({text}:{text:string}){return <StatusBadge text={text}/>;}
